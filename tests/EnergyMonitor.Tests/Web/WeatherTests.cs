using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.FakeUpstreams;
using EnergyMonitor.Web.HeatPump;
using EnergyMonitor.Web.Weather;

namespace EnergyMonitor.Tests.Web;

/// <summary>
/// Outdoor temperature from the fake Open-Meteo and the analysis of consumption against it.
/// "Today" is 8 November 2026, 08:05; the fake has consumption through 6 November.
/// </summary>
public sealed class WeatherTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static OpenMeteoClient Client(HttpStatusCode status, string body, Action<HttpRequestMessage>? inspect = null) =>
        new(new Factory(new HttpClient(new Handler(request =>
        {
            inspect?.Invoke(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        })) { BaseAddress = new Uri("https://fake/") }));

    private static Task<HttpResponseMessage> SetLocationAsync(HttpClient admin, string id, double? latitude, double? longitude) =>
        admin.PutAsJsonAsync($"/api/admin/properties/{id}", new { name = id == "hjem" ? "Hjem" : "Sommerhus", monitoringEnabled = true, latitude, longitude });

    [Fact]
    public async Task Client_AsksForHourlyTemperature_AndLeavesOutHoursWithoutAValue()
    {
        var client = Client(HttpStatusCode.OK,
            """{"utc_offset_seconds":0,"hourly_units":{"time":"unixtime","temperature_2m":"°C"},"hourly":{"time":[1794009600,1794013200,1794016800],"temperature_2m":[4.3,null,-1.5]}}""",
            request => Assert.Equal("/v1/forecast?latitude=55.9321&longitude=11.6&hourly=temperature_2m&past_days=92&forecast_days=1&timeformat=unixtime", request.RequestUri!.PathAndQuery));

        var hours = await client.FetchAsync(55.93214, 11.6, 500, default);

        Assert.Equal([(DateTimeOffset.FromUnixTimeSeconds(1794009600), 4.3), (DateTimeOffset.FromUnixTimeSeconds(1794016800), -1.5)], hours);
    }

    [Fact]
    public async Task Client_ReadsIsoTimesAsUtc_AndNamesFailuresWithoutTheSourceText()
    {
        var iso = await Client(HttpStatusCode.OK, """{"hourly":{"time":["2026-11-08T07:00"],"temperature_2m":[6]}}""").FetchAsync(55, 11, 1, default);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 7, 0, 0, TimeSpan.Zero), iso[0].HourUtc);

        var limited = await Assert.ThrowsAsync<MeasurementSourceException>(() =>
            Client(HttpStatusCode.TooManyRequests, """{"error":true,"reason":"Daily API request limit exceeded."}""").FetchAsync(55, 11, 1, default));
        Assert.Equal("weather_rate_limited", limited.ErrorCode);
        Assert.DoesNotContain("Daily", limited.Message);

        var failed = await Assert.ThrowsAsync<MeasurementSourceException>(() => Client(HttpStatusCode.BadGateway, "").FetchAsync(55, 11, 1, default));
        Assert.Equal("weather_http_502", failed.ErrorCode);
        var odd = await Assert.ThrowsAsync<MeasurementSourceException>(() => Client(HttpStatusCode.OK, """{"hourly":{}}""").FetchAsync(55, 11, 1, default));
        Assert.Equal("weather_bad_response", odd.ErrorCode);
    }

    [Fact]
    public async Task Analysis_FindsWhatADegreeCosts_WhenConsumptionFollowsTheOutdoorTemperature()
    {
        using var app = new AppFactory();
        await app.SeedPropertiesAsync();
        app.Fake.ApplyScenario("heating");
        using var admin = app.ClientFor(AppFactory.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await SetLocationAsync(admin, "sommerhus", 55.93, 11.6)).StatusCode);

        await app.SyncAsync();
        using var viewer = app.ClientFor(AppFactory.Viewer);
        var view = await viewer.GetJsonAsync("/api/properties/sommerhus/heating");

        Assert.Equal("Ok", view.GetProperty("status").GetString());
        Assert.False(view.GetProperty("usesIndoor").GetBoolean());
        Assert.Equal(17, view.GetProperty("baseTemperatureC").GetDouble());
        Assert.Equal(90, view.GetProperty("periodDays").GetInt32());
        var model = view.GetProperty("model");
        Assert.InRange(model.GetProperty("kWhPerDegreeDay").GetDouble(), FakeState.HeatingKWhPerDegreeDay - 0.08, FakeState.HeatingKWhPerDegreeDay + 0.08);
        Assert.InRange(model.GetProperty("baseKWhPerDay").GetDouble(), 1.0, 3.0);
        Assert.InRange(model.GetProperty("rSquared").GetDouble(), 0.85, 1.0);
        // Complete consumption runs through 6 November; the 89 days before that all have a temperature for every hour.
        Assert.Equal(89, model.GetProperty("days").GetInt32());
        var latest = view.GetProperty("latest");
        Assert.Equal("2026-11-06", latest.GetProperty("date").GetString());
        Assert.Equal(latest.GetProperty("kWh").GetDouble(), latest.GetProperty("expectedKWh").GetDouble(), 0);
        Assert.Equal(17 - latest.GetProperty("outdoorC").GetDouble(), latest.GetProperty("degreeDays").GetDouble(), 6);
        Assert.Equal(JsonValueKind.String, view.GetProperty("lastWeatherSyncUtc").ValueKind);

        var properties = await viewer.GetJsonAsync("/api/properties");
        Assert.Equal(55.93, properties.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "sommerhus").GetProperty("latitude").GetDouble());
    }

    [Fact]
    public async Task WithoutAPosition_NothingIsFetched_AndTheAnalysisSaysSo()
    {
        using var app = new AppFactory();
        await app.SeedPropertiesAsync();

        await app.SyncAsync();
        using var viewer = app.ClientFor(AppFactory.Viewer);
        var view = await viewer.GetJsonAsync("/api/properties/sommerhus/heating");

        Assert.Equal(0, app.Fake.WeatherCalls);
        Assert.Equal("NoLocation", view.GetProperty("status").GetString());
        Assert.Equal(0, view.GetProperty("days").GetArrayLength());
    }

    [Fact]
    public async Task OnlyHoursThatHaveHappened_AreStored_AndMovingThePropertyRemovesThem()
    {
        using var app = new AppFactory();
        await app.SeedPropertiesAsync();
        using var admin = app.ClientFor(AppFactory.Admin);
        await SetLocationAsync(admin, "hjem", 55.7, 12.57);
        await app.SyncAsync();
        var store = app.Get<WeatherStore>();
        var now = app.Time.GetUtcNow();

        var stored = await store.GetAsync("hjem", now.AddDays(-100), now.AddDays(2), default);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero), stored.Keys.Max()); // 07–08 Danish time is the last hour that is over
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero), stored.Keys.Min()); // 92 days back
        Assert.Equal(app.Fake.OutdoorC(stored.Keys.Max()), stored[stored.Keys.Max()]);
        Assert.Equal(1, app.Fake.WeatherCalls); // Sommerhus has no position

        await SetLocationAsync(admin, "hjem", 55.7, 12.57); // saved again unchanged
        Assert.NotEmpty(await store.GetAsync("hjem", now.AddDays(-100), now, default));

        await SetLocationAsync(admin, "hjem", 57.05, 9.92);
        Assert.Empty(await store.GetAsync("hjem", now.AddDays(-100), now, default));
        Assert.Null((await store.GetStatusAsync("hjem", default)).LastSuccessUtc);

        await SetLocationAsync(admin, "hjem", null, null);
        Assert.Null((await app.Get<IPropertyStore>().GetAsync("hjem", default))!.Latitude);
    }

    [Fact]
    public async Task AWeatherFailure_IsRecorded_AndDoesNotFailTheConsumptionSync()
    {
        using var app = new AppFactory();
        await app.SeedPropertiesAsync();
        using var admin = app.ClientFor(AppFactory.Admin);
        await SetLocationAsync(admin, "sommerhus", 55.93, 11.6);
        app.Fake.WeatherDown = true;

        var run = await app.SyncAsync();
        var view = await admin.GetJsonAsync("/api/properties/sommerhus/heating");

        Assert.Equal(SyncStatus.Succeeded, run!.Status);
        Assert.Equal("weather_rate_limited", view.GetProperty("weatherErrorCode").GetString());
        Assert.Equal("InsufficientData", view.GetProperty("status").GetString());
        Assert.Empty(app.Telegrams);

        // The next successful fetch clears the error and keeps nothing of it.
        app.Fake.WeatherDown = false;
        await app.SyncAsync();
        view = await admin.GetJsonAsync("/api/properties/sommerhus/heating");
        Assert.False(view.TryGetProperty("weatherErrorCode", out var code) && code.ValueKind != JsonValueKind.Null);
        Assert.Equal("Ok", view.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ALinkedHeatPump_MakesTheAnalysisUseTheRecordedIndoorTemperature()
    {
        using var app = new AppFactory(heatpump: true);
        await app.SeedPropertiesAsync();
        await app.LinkHeatPumpAsync();
        using var admin = app.ClientFor(AppFactory.Admin);
        await SetLocationAsync(admin, "sommerhus", 55.93, 11.6);
        // 30 days of indoor readings at 10 °C, four an hour; the two days before have readings in too few hours.
        var start = new DateTimeOffset(2026, 10, 5, 22, 0, 0, TimeSpan.Zero);
        var points = new List<HeatPumpPoint>();
        for (var t = start; t < start.AddDays(32); t = t.AddMinutes(15))
        {
            if (t >= start.AddDays(2) || t.Hour < 10) points.Add(new(t.ToUnixTimeSeconds(), 10, 60));
        }

        await app.Get<HeatPumpStore>().UpsertAsync(FakeSensibo.DeviceId, points, default);
        await app.SyncAsync();

        var view = await admin.GetJsonAsync("/api/properties/sommerhus/heating");

        Assert.True(view.GetProperty("usesIndoor").GetBoolean());
        var days = view.GetProperty("days").EnumerateArray().ToArray();
        Assert.Equal(30, days.Length);
        Assert.Equal("2026-10-08", days[0].GetProperty("date").GetString());
        Assert.All(days, d =>
        {
            Assert.Equal(10, d.GetProperty("indoorC").GetDouble());
            Assert.Equal(Math.Max(0, 10 - d.GetProperty("outdoorC").GetDouble()), d.GetProperty("degreeDays").GetDouble(), 6);
        });
    }

    [Fact]
    public async Task PositionAndPeriod_AreValidated_AndReadingNeedsSignIn()
    {
        using var app = new AppFactory();
        await app.SeedPropertiesAsync();
        using var admin = app.ClientFor(AppFactory.Admin);
        using var viewer = app.ClientFor(AppFactory.Viewer);
        using var anonymous = app.ClientFor(null);

        foreach (var (latitude, longitude) in new (double?, double?)[] { (55.9, null), (null, 11.6), (91, 11.6), (55.9, -181) })
        {
            var response = await SetLocationAsync(admin, "sommerhus", latitude, longitude);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_location", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await SetLocationAsync(viewer, "sommerhus", 55.9, 11.6)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/properties/sommerhus/heating")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/properties/sommerhus/heating?days=29")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/properties/sommerhus/heating?days=366")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/properties/sommerhus/heating?days=365")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/properties/absent/heating")).StatusCode);
    }
}
