using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EnergyMonitor.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Web.Eloverblik;

public sealed class EloverblikOptions
{
    public string BaseUrl { get; set; } = "https://api.eloverblik.dk/customerapi/api/";

    /// <summary>Personal refresh token created in the Eloverblik portal. Comes from Key Vault or user secrets.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>First wait after 429/503. Eloverblik asks for at least one minute.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 60;

    public int MaxAttempts { get; set; } = 4;

    public string PreferredAggregation { get; set; } = "Hour";
}

/// <summary>Waits between retries; replaced in tests so they do not sleep.</summary>
public interface IDelayer
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class TaskDelayer(TimeProvider time) : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, time, ct);
}

public sealed record MeteringPoint(
    [property: JsonPropertyName("meteringPointId")] string MeteringPointId,
    [property: JsonPropertyName("typeOfMP")] string? TypeOfMP,
    [property: JsonPropertyName("streetName")] string? StreetName,
    [property: JsonPropertyName("buildingNumber")] string? BuildingNumber,
    [property: JsonPropertyName("postcode")] string? Postcode,
    [property: JsonPropertyName("cityName")] string? CityName,
    [property: JsonPropertyName("meterReadingOccurrence")] string? MeterReadingOccurrence);

/// <summary>One point of a time series, already placed on the UTC time line.</summary>
public sealed record TimeSeriesPoint(DateTimeOffset StartUtc, DateTimeOffset EndUtc, double? Quantity, string QualityCode, string Resolution);

/// <summary>
/// Client for the Eloverblik Customer API, following the published Swagger (V1.0):
/// GET token, GET meteringpoints/meteringpoints, POST meterdata/gettimeseries/{from}/{to}/{aggregation}.
/// Errors surface as <see cref="MeasurementSourceException"/> with a short code and never contain a token
/// or a full metering point id.
/// </summary>
public sealed partial class EloverblikClient(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<EloverblikOptions> options,
    IDelayer delayer,
    TimeProvider time,
    ILogger<EloverblikClient> logger)
{
    public const string HttpClientName = "eloverblik";
    public const int NoDataAvailable = 30015;
    public const int AggregationNotSupported = 30008;
    public const int OutsideAvailablePeriod = 30018;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private string? _accessTokenFor;
    private DateTimeOffset _accessTokenExpires;

    /// <summary>Last digits only: enough to recognise a metering point in a log, not enough to identify it.</summary>
    public static string Mask(string? meteringPointId) =>
        string.IsNullOrEmpty(meteringPointId) ? "(ingen)"
        : meteringPointId.Length <= 4 ? "****"
        : $"…{meteringPointId[^4..]}";

    public async Task<IReadOnlyList<MeteringPoint>> GetMeteringPointsAsync(CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "meteringpoints/meteringpoints?includeAll=true"), ct);
        var body = await response.Content.ReadFromJsonAsync<ResultEnvelope<List<MeteringPoint>>>(Json, ct);
        return body?.Result ?? [];
    }

    /// <summary>
    /// Returns the points Eloverblik has for the period. A metering point without data in the period
    /// yields an empty list, which callers must treat as missing, not as zero consumption.
    /// </summary>
    public async Task<IReadOnlyList<TimeSeriesPoint>> GetTimeSeriesAsync(
        string meteringPointId, DateOnly from, DateOnly to, string aggregation, TimeZoneInfo zone, CancellationToken ct)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"meterdata/gettimeseries/{from:yyyy-MM-dd}/{to:yyyy-MM-dd}/{aggregation}");
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new { meteringPoints = new { meteringPoint = new[] { meteringPointId } } }),
            },
            ct);

        var body = await response.Content.ReadFromJsonAsync<ResultEnvelope<List<TimeSeriesResult>>>(Json, ct);
        var points = new List<TimeSeriesPoint>();
        foreach (var result in body?.Result ?? [])
        {
            if (!result.Success)
            {
                if (result.ErrorCode is NoDataAvailable or OutsideAvailablePeriod)
                {
                    continue;
                }

                LogResultError(logger, Mask(meteringPointId), result.ErrorCode);
                throw new MeasurementSourceException($"eloverblik_{result.ErrorCode}", $"Eloverblik returned error {result.ErrorCode} for metering point {Mask(meteringPointId)}.");
            }

            foreach (var period in result.Document?.TimeSeries?.SelectMany(t => t.Period ?? []) ?? [])
            {
                points.AddRange(ReadPeriod(period, zone));
            }
        }

        return points;
    }

    public async Task<(string Json, bool Historical)> GetChargeEvidenceAsync(string meter, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var zone = Core.Time.LocalDay.Zone(Core.Model.Property.DefaultTimeZone);
        try
        {
            using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Post,
                "meteringpoints/meteringpoint/getchargelinkswithcharges")
            {
                Content = JsonContent.Create(new { query = new[] { new { meteringPointId = meter,
                    from = Core.Time.LocalDay.StartUtc(from, zone), to = Core.Time.LocalDay.StartUtc(to, zone) } } }),
            }, ct);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            // Remove meter identities from stored evidence and exported configuration.
            if (!document.RootElement.TryGetProperty("result", out var root)
                || !root.TryGetProperty("results", out var results)
                || !results.EnumerateArray().Any()
                || results.EnumerateArray().Any(r => r.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString())))
                throw new MeasurementSourceException("charge_result_failed", "Charge history is unavailable for the requested meter.");
            var node = System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!;
            RemoveMeterIds(node);
            return (node.ToJsonString(), true);
        }
        catch (MeasurementSourceException ex) when (ex.ErrorCode == "http_404")
        {
            using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Post,
                "meteringpoints/meteringpoint/getcharges")
            {
                Content = JsonContent.Create(new { meteringPoints = new { meteringPoint = new[] { meter } } }),
            }, ct);
            var node = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            RemoveMeterIds(node);
            return (node.ToJsonString(), false);
        }
    }

    private static void RemoveMeterIds(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            obj.Remove("meteringPointId");
            foreach (var child in obj.ToArray()) if (child.Value is not null) RemoveMeterIds(child.Value);
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
            foreach (var child in array) if (child is not null) RemoveMeterIds(child);
    }

    internal static IEnumerable<TimeSeriesPoint> ReadPeriod(PeriodDto period, TimeZoneInfo zone)
    {
        if (period.TimeInterval is not { } interval || period.Resolution is null || period.Point is null)
        {
            yield break;
        }

        TimeSpan? step = period.Resolution switch
        {
            "PT1H" => TimeSpan.FromHours(1),
            "PT15M" => TimeSpan.FromMinutes(15),
            _ => null,
        };

        foreach (var point in period.Point)
        {
            if (!int.TryParse(point.Position, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) || position < 1)
            {
                continue;
            }

            DateTimeOffset start, end;
            if (step is { } s)
            {
                start = interval.Start.ToUniversalTime() + (s * (position - 1));
                end = start + s;
            }
            else if (period.Resolution == "P1D")
            {
                // Daily points follow local calendar days, which are not all 24 hours long.
                var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(interval.Start, zone).DateTime);
                start = Core.Time.LocalDay.StartUtc(firstDay.AddDays(position - 1), zone);
                end = Core.Time.LocalDay.StartUtc(firstDay.AddDays(position), zone);
            }
            else
            {
                // Monthly, yearly and profiled series cannot be placed on a day and are not used.
                continue;
            }

            double? quantity = double.TryParse(point.Quantity, NumberStyles.Float, CultureInfo.InvariantCulture, out var q) ? q : null;
            yield return new TimeSeriesPoint(start, end, quantity, point.Quality ?? "", period.Resolution);
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetAccessTokenAsync(ct);
            var response = await SendWithRetryAsync(create, token, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return await EnsureSuccessAsync(response, ct);
            }

            response.Dispose();
            _accessToken = null;
            if (attempt > 0)
            {
                throw new MeasurementSourceException(MeasurementSourceException.TokenInvalid, "Eloverblik rejected the data access token.");
            }
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var refreshToken = options.CurrentValue.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new MeasurementSourceException("token_missing", "No Eloverblik refresh token is configured.");
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && _accessTokenFor == refreshToken && time.GetUtcNow() < _accessTokenExpires)
            {
                return _accessToken;
            }

            // The data access token lasts 24 hours and the endpoint allows two calls a minute, so it is
            // fetched once and reused.
            using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, "token"), refreshToken, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new MeasurementSourceException(MeasurementSourceException.TokenInvalid, "Eloverblik rejected the refresh token (expired or revoked).");
            }

            using var ok = await EnsureSuccessAsync(response, ct);
            var body = await ok.Content.ReadFromJsonAsync<ResultEnvelope<string>>(Json, ct);
            if (string.IsNullOrWhiteSpace(body?.Result))
            {
                throw new MeasurementSourceException(MeasurementSourceException.TokenInvalid, "Eloverblik returned no data access token.");
            }

            _accessToken = body.Result;
            _accessTokenFor = refreshToken;
            var now = time.GetUtcNow();
            var jwtExpiry = JwtExpiry.TryRead(_accessToken);
            _accessTokenExpires = jwtExpiry is { } exp && exp > now ? exp.AddMinutes(-5) : now.AddHours(20);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Retries 429 and 503 slowly: one minute, then doubling, for a limited number of attempts.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> create, string bearer, CancellationToken ct)
    {
        var o = options.CurrentValue;
        for (var attempt = 1; ; attempt++)
        {
            using var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            HttpResponseMessage response;
            try
            {
                response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new MeasurementSourceException("network_error", "Could not reach Eloverblik.", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new MeasurementSourceException("timeout", "Eloverblik did not answer in time.", ex);
            }

            if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            {
                return response;
            }

            var status = (int)response.StatusCode;
            response.Dispose();
            if (attempt >= o.MaxAttempts)
            {
                throw new MeasurementSourceException($"http_{status}", $"Eloverblik answered {status} on {attempt} attempts.");
            }

            var delay = TimeSpan.FromSeconds(o.RetryBaseDelaySeconds * Math.Pow(2, attempt - 1));
            LogRetry(logger, status, attempt, delay.TotalSeconds);
            await delayer.DelayAsync(delay, ct);
        }
    }

    private static async Task<HttpResponseMessage> EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        var code = ErrorCodeRegex().Match(body);
        throw new MeasurementSourceException(
            code.Success ? $"eloverblik_{code.Groups[1].Value}" : $"http_{status}",
            $"Eloverblik answered {status}.");
    }

    [GeneratedRegex("\"?errorCode\"?\\s*[:=]\\s*\"?(\\d{4,6})", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorCodeRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Eloverblik answered {Status} (attempt {Attempt}); retrying in {DelaySeconds} s")]
    private static partial void LogRetry(ILogger logger, int status, int attempt, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Eloverblik returned error {ErrorCode} for metering point {MeteringPoint}")]
    private static partial void LogResultError(ILogger logger, string meteringPoint, int errorCode);

    private sealed record ResultEnvelope<T>([property: JsonPropertyName("result")] T? Result);

    internal sealed record TimeSeriesResult(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("errorCode")] int ErrorCode,
        [property: JsonPropertyName("MyEnergyData_MarketDocument")] MarketDocumentDto? Document);

    internal sealed record MarketDocumentDto([property: JsonPropertyName("TimeSeries")] List<TimeSeriesDto>? TimeSeries);

    internal sealed record TimeSeriesDto([property: JsonPropertyName("Period")] List<PeriodDto>? Period);

    internal sealed record PeriodDto(
        [property: JsonPropertyName("resolution")] string? Resolution,
        [property: JsonPropertyName("timeInterval")] TimeIntervalDto? TimeInterval,
        [property: JsonPropertyName("Point")] List<PointDto>? Point);

    internal sealed record TimeIntervalDto(
        [property: JsonPropertyName("start")] DateTimeOffset Start,
        [property: JsonPropertyName("end")] DateTimeOffset End);

    internal sealed record PointDto(
        [property: JsonPropertyName("position")] string? Position,
        [property: JsonPropertyName("out_Quantity.quantity")] string? Quantity,
        [property: JsonPropertyName("out_Quantity.quality")] string? Quality);
}
