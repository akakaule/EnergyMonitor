using System.Net;
using System.Text;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Tests.Core;
using EnergyMonitor.Web.Eloverblik;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EnergyMonitor.Tests.Web;

public class EloverblikClientTests
{
    [Fact]
    public async Task Charges_FallBackToCurrentEvidence_WithoutTreatingItAsHistory()
    {
        var (client, handler, _, _) = Create((request, _) => TokenOr(request, () =>
            request.RequestUri!.AbsolutePath.EndsWith("getchargelinkswithcharges", StringComparison.Ordinal)
                ? Json("{}", HttpStatusCode.NotFound)
                : Json($$"""{"result":[{"meteringPointId":"{{MeteringPoint}}","tariffs":[]}]}""")));
        var evidence = await client.GetChargeEvidenceAsync(MeteringPoint, new(2026, 11, 1), new(2026, 11, 2), default);
        Assert.False(evidence.Historical);
        Assert.DoesNotContain(MeteringPoint, evidence.Json);
        Assert.EndsWith("/getcharges", handler.Requests[^1].Path);
        Assert.Contains("query", handler.Requests[^2].Body);
    }

    [Fact]
    public async Task Source_CanRequestQuarterlyValues_AndFallsBackToHours()
    {
        var (client, handler, _, _) = Create((request, _) => TokenOr(request, () =>
            request.RequestUri!.AbsolutePath.EndsWith("/Quarter", StringComparison.Ordinal)
                ? Json("""{"errorCode":30008}""", HttpStatusCode.BadRequest)
                : Json(Series(Period("2026-11-05T23:00:00Z", "2026-11-06T23:00:00Z", "PT1H", (1, "0.5", "A04"))))));
        var source = new EloverblikMeasurementSource(client, new Monitor(new EloverblikOptions { PreferredAggregation = "Quarter" }));
        var values = await source.FetchAsync(TestData.Sommerhus() with { MeteringPointId = MeteringPoint }, new(2026, 11, 5), new(2026, 11, 7), default);
        Assert.EndsWith("/Quarter", handler.Requests[^2].Path);
        Assert.EndsWith("/Hour", handler.Requests[^1].Path);
        Assert.Single(values);
    }

    private const string RefreshToken = "refresh-SECRET-token";
    private const string MeteringPoint = "571313174000000028";

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Bearer, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter, body));
            return respond(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://eloverblik.test/customerapi/api/") };
    }

    private sealed class Monitor(EloverblikOptions value) : IOptionsMonitor<EloverblikOptions>
    {
        public EloverblikOptions CurrentValue => value;

        public EloverblikOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EloverblikOptions, string?> listener) => null;
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Series(string periods) =>
        "{\"result\":[{\"success\":true,\"errorCode\":10000,\"errorText\":\"NoError\",\"id\":\"" + MeteringPoint + "\",\"MyEnergyData_MarketDocument\":{\"TimeSeries\":[{\"mRID\":\"" + MeteringPoint + "\",\"businessType\":\"A04\",\"Period\":[" + periods + "]}]}}]}";

    private static string Period(string start, string end, string resolution, params (int Position, string Quantity, string Quality)[] points) =>
        $$"""{"resolution":"{{resolution}}","timeInterval":{"start":"{{start}}","end":"{{end}}"},"Point":[{{string.Join(",", points.Select(p => $$"""{"position":"{{p.Position}}","out_Quantity.quantity":"{{p.Quantity}}","out_Quantity.quality":"{{p.Quality}}"}"""))}}]}""";

    private static (EloverblikClient Client, StubHandler Handler, RecordingDelayer Delayer, CapturingLoggerProvider Logs) Create(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond, FakeTimeProvider? time = null)
    {
        var handler = new StubHandler(respond);
        var delayer = new RecordingDelayer();
        var logs = new CapturingLoggerProvider();
        var client = new EloverblikClient(
            new Factory(handler), new Monitor(new EloverblikOptions { RefreshToken = RefreshToken }), delayer,
            time ?? new FakeTimeProvider(TestData.Noon(new DateOnly(2026, 11, 8))),
            LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)).CreateLogger<EloverblikClient>());
        return (client, handler, delayer, logs);
    }

    private static HttpResponseMessage TokenOr(HttpRequestMessage request, Func<HttpResponseMessage> data) =>
        request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) ? Json("""{"result":"access-1"}""") : data();

    private static Task<IReadOnlyList<TimeSeriesPoint>> Fetch(EloverblikClient client, string aggregation = "Hour") =>
        client.GetTimeSeriesAsync(MeteringPoint, new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 7), aggregation, TestData.Zone, CancellationToken.None);

    [Fact]
    public async Task ValidToken_ExchangesRefreshToken_ThenCallsTimeSeriesAsDocumented()
    {
        var (client, handler, _, _) = Create((request, _) => TokenOr(request, () => Json(Series(
            Period("2026-11-05T23:00:00Z", "2026-11-06T23:00:00Z", "PT1H", (1, "0.250", "A04"), (2, "0.300", "A03"))))));

        var points = await Fetch(client);

        Assert.Equal(("GET", "/customerapi/api/token", RefreshToken), (handler.Requests[0].Method, handler.Requests[0].Path, handler.Requests[0].Bearer));
        var data = handler.Requests[1];
        Assert.Equal(("POST", "/customerapi/api/meterdata/gettimeseries/2026-11-05/2026-11-07/Hour", "access-1"), (data.Method, data.Path, data.Bearer));
        Assert.Equal("{\"meteringPoints\":{\"meteringPoint\":[\"" + MeteringPoint + "\"]}}", data.Body);

        Assert.Equal(2, points.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.Zero), points[0].StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), points[0].EndUtc);
        Assert.Equal(0.25, points[0].Quantity);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), points[1].StartUtc);
        Assert.Equal("A03", points[1].QualityCode);
    }

    [Fact]
    public async Task AccessToken_IsFetchedOnce_AndReused()
    {
        var (client, handler, _, _) = Create((request, _) => TokenOr(request, () => Json(Series(""))));

        await Fetch(client);
        await Fetch(client);
        await Fetch(client);

        Assert.Single(handler.Requests, r => r.Path.EndsWith("/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AccessToken_IsRenewedAfterItsLifetime()
    {
        var time = new FakeTimeProvider(TestData.Noon(new DateOnly(2026, 11, 8)));
        var (client, handler, _, _) = Create((request, _) => TokenOr(request, () => Json(Series(""))), time);

        await Fetch(client);
        time.Advance(TimeSpan.FromHours(21));
        await Fetch(client);

        Assert.Equal(2, handler.Requests.Count(r => r.Path.EndsWith("/token", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ExpiredRefreshToken_IsReportedAsTokenInvalid_WithoutRetrying(HttpStatusCode status)
    {
        var (client, handler, delayer, _) = Create((_, _) => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<MeasurementSourceException>(() => Fetch(client));

        Assert.Equal(MeasurementSourceException.TokenInvalid, ex.ErrorCode);
        Assert.Single(handler.Requests);
        Assert.Empty(delayer.Delays);
    }

    [Fact]
    public async Task RejectedAccessToken_IsRenewedOnce_ThenTheCallSucceeds()
    {
        var dataCalls = 0;
        var (client, handler, _, _) = Create((request, _) => TokenOr(request, () =>
            ++dataCalls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(Series(""))));

        await Fetch(client);

        Assert.Equal(2, handler.Requests.Count(r => r.Path.EndsWith("/token", StringComparison.Ordinal)));
        Assert.Equal(2, dataCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ThrottledOrUnavailable_WaitsAtLeastAMinute_BacksOffExponentially_ThenSucceeds(HttpStatusCode status)
    {
        var dataCalls = 0;
        var (client, _, delayer, _) = Create((request, _) => TokenOr(request, () =>
            ++dataCalls <= 2 ? new HttpResponseMessage(status) : Json(Series(""))));

        await Fetch(client);

        Assert.Equal([TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)], delayer.Delays);
        Assert.Equal(3, dataCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "http_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "http_503")]
    public async Task PersistentThrottling_GivesUpAfterALimitedNumberOfAttempts(HttpStatusCode status, string code)
    {
        var dataCalls = 0;
        var (client, _, delayer, _) = Create((request, _) => TokenOr(request, () =>
        {
            dataCalls++;
            return new HttpResponseMessage(status);
        }));

        var ex = await Assert.ThrowsAsync<MeasurementSourceException>(() => Fetch(client));

        Assert.Equal(code, ex.ErrorCode);
        Assert.Equal(4, dataCalls);
        Assert.Equal([TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(240)], delayer.Delays);
    }

    [Fact]
    public async Task NoDataAvailable_IsAnEmptyResult_NotAnErrorAndNotZero()
    {
        var (client, _, _, _) = Create((request, _) => TokenOr(request, () => Json(
            $$"""{"result":[{"success":false,"errorCode":30015,"errorText":"NoDataAvailable","id":"{{MeteringPoint}}"}]}""")));

        Assert.Empty(await Fetch(client));
    }

    [Fact]
    public async Task OtherResultErrors_AreReportedWithTheirCode_AndAMaskedMeteringPoint()
    {
        var (client, _, _, logs) = Create((request, _) => TokenOr(request, () => Json(
            $$"""{"result":[{"success":false,"errorCode":20008,"errorText":"MeteringPointNotFound","id":"{{MeteringPoint}}"}]}""")));

        var ex = await Assert.ThrowsAsync<MeasurementSourceException>(() => Fetch(client));

        Assert.Equal("eloverblik_20008", ex.ErrorCode);
        Assert.DoesNotContain(MeteringPoint, ex.Message);
        Assert.Contains("…0028", ex.Message);
        Assert.DoesNotContain(MeteringPoint, logs.Text);
    }

    [Fact]
    public async Task RequestLevelError_CarriesTheEloverblikErrorCode()
    {
        var (client, _, _, _) = Create((request, _) => TokenOr(request, () =>
            Json("""{"errorCode":30008,"errorText":"AggregationNotSupported"}""", HttpStatusCode.BadRequest)));

        var ex = await Assert.ThrowsAsync<MeasurementSourceException>(() => Fetch(client));

        Assert.Equal("eloverblik_30008", ex.ErrorCode);
    }

    [Fact]
    public async Task HourlyPeriods_OnDstDays_Have23And25Points()
    {
        static (int, string, string)[] Hours(int n) => [.. Enumerable.Range(1, n).Select(i => (i, "0.5", "A04"))];
        var (client, _, _, _) = Create((request, _) => TokenOr(request, () => Json(Series(
            Period("2026-03-28T23:00:00Z", "2026-03-29T22:00:00Z", "PT1H", Hours(23)) + "," +
            Period("2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z", "PT1H", Hours(25))))));

        var points = await Fetch(client);

        Assert.Equal(48, points.Count);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero), points[22].EndUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), points[^1].EndUtc);
        Assert.Equal(points.Count, points.Select(p => p.StartUtc).Distinct().Count());
    }

    [Fact]
    public async Task DailyPeriod_FollowsLocalDays_AcrossADstChange()
    {
        var (client, _, _, _) = Create((request, _) => TokenOr(request, () => Json(Series(
            Period("2026-10-23T22:00:00Z", "2026-10-26T23:00:00Z", "P1D", (1, "8", "A04"), (2, "9", "A04"), (3, "10", "A04"))))));

        var points = await Fetch(client, "Day");

        Assert.Equal(3, points.Count);
        Assert.Equal(24, (points[0].EndUtc - points[0].StartUtc).TotalHours);
        Assert.Equal(25, (points[1].EndUtc - points[1].StartUtc).TotalHours); // 25 October 2026
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 23, 0, 0, TimeSpan.Zero), points[2].EndUtc);
    }

    [Fact]
    public async Task Source_MapsQualityCodes_AndNeverTurnsAMissingValueIntoANumber()
    {
        var (client, _, _, _) = Create((request, _) => TokenOr(request, () => Json(Series(
            Period("2026-11-05T23:00:00Z", "2026-11-06T23:00:00Z", "PT1H",
                (1, "0.5", "A04"), (2, "0.5", "A03"), (3, "0.5", "A01"), (4, "0", "A02"), (5, "0.2", "A05"), (6, "", "A04"))))));
        var source = new EloverblikMeasurementSource(client);

        var m = await source.FetchAsync(TestData.Sommerhus() with { MeteringPointId = MeteringPoint }, new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 7), CancellationToken.None);

        Assert.Equal(
            [IntervalQuality.Measured, IntervalQuality.Estimated, IntervalQuality.Adjusted, IntervalQuality.Missing, IntervalQuality.Incomplete, IntervalQuality.Missing],
            m.Select(x => x.Quality));
        Assert.Null(m[3].Value);
        Assert.Null(m[5].Value);
        Assert.All(m, x => Assert.Equal(("eloverblik", "energy.consumption", "kWh", "PT1H"), (x.SourceId, x.Metric, x.Unit, x.Resolution)));
    }

    [Fact]
    public async Task Source_FallsBackToDailyValues_WhenHourlyIsNotSupported()
    {
        var (client, handler, _, _) = Create((request, _) => TokenOr(request, () =>
            request.RequestUri!.AbsolutePath.EndsWith("/Hour", StringComparison.Ordinal)
                ? Json("""{"errorCode":30008}""", HttpStatusCode.BadRequest)
                : Json(Series(Period("2026-11-05T23:00:00Z", "2026-11-06T23:00:00Z", "P1D", (1, "7.5", "A04"))))));
        var source = new EloverblikMeasurementSource(client);

        var m = await source.FetchAsync(TestData.Sommerhus() with { MeteringPointId = MeteringPoint }, new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 7), CancellationToken.None);

        var only = Assert.Single(m);
        Assert.Equal("P1D", only.Resolution);
        Assert.Equal(24, (only.IntervalEndUtc!.Value - only.ObservedAtUtc).TotalHours);
        Assert.EndsWith("/Day", handler.Requests[^1].Path);
    }

    [Fact]
    public async Task MeteringPoints_AreListed()
    {
        var (client, handler, _, _) = Create((request, _) => TokenOr(request, () => Json(
            $$"""{"result":[{"meteringPointId":"{{MeteringPoint}}","typeOfMP":"E17","streetName":"Klitvejen","buildingNumber":"3","postcode":"4573","cityName":"Højby","meterReadingOccurrence":"PT1H"}]}""")));

        var point = Assert.Single(await client.GetMeteringPointsAsync(CancellationToken.None));

        Assert.Equal(MeteringPoint, point.MeteringPointId);
        Assert.Equal("Klitvejen", point.StreetName);
        Assert.Equal("/customerapi/api/meteringpoints/meteringpoints", handler.Requests[^1].Path);
    }

    [Fact]
    public async Task Logs_NeverContainTokens()
    {
        var dataCalls = 0;
        var (client, _, _, logs) = Create((request, _) => TokenOr(request, () =>
            ++dataCalls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(Series(""))));

        await Fetch(client);

        Assert.NotEmpty(logs.Text);
        Assert.DoesNotContain(RefreshToken, logs.Text);
        Assert.DoesNotContain("access-1", logs.Text);
    }

    [Fact]
    public void JwtExpiry_ReadsExp_AndToleratesGarbage()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"exp":1800000000}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), JwtExpiry.TryRead($"e30.{payload}.sig"));
        Assert.Null(JwtExpiry.TryRead("not-a-jwt"));
        Assert.Null(JwtExpiry.TryRead("a.%%%.c"));
        Assert.Null(JwtExpiry.TryRead(null));
    }
}
