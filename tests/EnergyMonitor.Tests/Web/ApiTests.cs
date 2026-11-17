using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.FakeUpstreams;
using EnergyMonitor.Web.Auth;

namespace EnergyMonitor.Tests.Web;

public class ApiTests : IAsyncLifetime
{
    private readonly AppFactory _app = new(heatpump: true);

    public async ValueTask InitializeAsync()
    {
        _app.Fake.DayFactors[(FakeState.Sommerhus, new DateOnly(2026, 11, 6))] = 0.15;
        await _app.SeedPropertiesAsync();
        await _app.SyncAsync();
    }

    public ValueTask DisposeAsync()
    {
        _app.Dispose();
        return ValueTask.CompletedTask;
    }

    // A body is sent with PUT so the request reaches the endpoint rather than failing content negotiation first.
    private static HttpRequestMessage Request(string method, string url) =>
        new(new HttpMethod(method), url) { Content = method == "PUT" ? JsonContent.Create(new { }) : null };

    public static TheoryData<string, string> PrivateEndpoints => new()
    {
        { "GET", "/api/me" },
        { "GET", "/api/properties" },
        { "GET", "/api/properties/sommerhus/consumption?from=2026-11-01&to=2026-11-07&resolution=day" },
        { "GET", "/api/properties/sommerhus/alerts" },
        { "GET", "/api/alerts?state=open" },
        { "POST", "/api/alerts/sommerhus~LowConsumption~2026-11-06/acknowledge" },
        { "GET", "/api/properties/sommerhus/rules" },
        { "PUT", "/api/properties/sommerhus/rules/LowConsumption" },
        { "GET", "/api/system/health" },
        { "POST", "/api/admin/sync" },
        { "POST", "/api/admin/notifications/test" },
        { "GET", "/api/admin/settings/notifications" },
        { "GET", "/api/admin/metering-points" },
        { "GET", "/api/admin/export" },
        { "GET", "/api/admin/report" },
        { "DELETE", "/api/admin/properties/sommerhus/data" },
        { "GET", "/api/heatpump" },
        { "GET", "/api/heatpump/history/Pod1Stue?days=1" },
        { "GET", "/api/heatpump/sensibo/users/me/pods" },
        { "POST", "/api/heatpump/sensibo/pods/Pod1Stue/acStates" },
        { "PUT", "/api/admin/heatpump/links" },
        { "POST", "/api/admin/heatpump/collect" },
        { "GET", "/api/does-not-exist" },
    };

    [Theory]
    [MemberData(nameof(PrivateEndpoints))]
    public async Task Unauthenticated_IsRejectedEverywhere(string method, string url)
    {
        using var client = _app.ClientFor(null);

        var response = await client.SendAsync(Request(method, url));

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound, $"{method} {url} → {(int)response.StatusCode}");
        Assert.DoesNotContain("kWh", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        if (!url.Contains("does-not-exist", StringComparison.Ordinal))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [MemberData(nameof(PrivateEndpoints))]
    public async Task SignedInButNotOnTheAllowlist_IsForbiddenEverywhere(string method, string url)
    {
        using var client = _app.ClientFor(AppFactory.Stranger);

        var response = await client.SendAsync(Request(method, url));

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{method} {url} → {(int)response.StatusCode}");
        if (!url.Contains("does-not-exist", StringComparison.Ordinal))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("POST", "/api/admin/sync")]
    [InlineData("POST", "/api/admin/notifications/test")]
    [InlineData("GET", "/api/admin/export")]
    [InlineData("GET", "/api/admin/metering-points")]
    [InlineData("PUT", "/api/admin/settings/notifications")]
    [InlineData("PUT", "/api/admin/properties/sommerhus")]
    [InlineData("DELETE", "/api/admin/properties/sommerhus/data")]
    [InlineData("PUT", "/api/properties/sommerhus/rules/LowConsumption")]
    [InlineData("PUT", "/api/admin/heatpump/links")]
    [InlineData("POST", "/api/admin/heatpump/collect")]
    [InlineData("POST", "/api/heatpump/sensibo/pods/Pod1Stue/acStates")]
    [InlineData("PUT", "/api/heatpump/sensibo/pods/Pod1Stue/timer/")]
    [InlineData("DELETE", "/api/heatpump/sensibo/pods/Pod1Stue/timer/")]
    [InlineData("PUT", "/api/heatpump/sensibo/pods/Pod1Stue/smartmode")]
    public async Task Viewer_CannotUseAdminEndpoints(string method, string url)
    {
        using var client = _app.ClientFor(AppFactory.Viewer);

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StateChangingCall_WithoutTheRequestHeader_IsRefused()
    {
        using var client = _app.ClientFor(AppFactory.Admin);
        client.DefaultRequestHeaders.Remove(AuthSetup.CsrfHeader);

        var response = await client.PostAsync("/api/admin/notifications/test", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_app.Telegrams, t => t.Contains("testbesked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Me_ReturnsTheRoleFromTheAllowlist()
    {
        using var viewer = _app.ClientFor(AppFactory.Viewer);
        using var admin = _app.ClientFor(AppFactory.Admin);

        Assert.Equal("viewer", (await viewer.GetJsonAsync("/api/me")).GetProperty("role").GetString());
        Assert.Equal("admin", (await admin.GetJsonAsync("/api/me")).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Properties_ShowLatestCompleteDay_Status_Freshness_AndAMaskedMeteringPoint()
    {
        using var client = _app.ClientFor(AppFactory.Viewer);

        var json = await client.GetAsync("/api/properties");
        var raw = await json.Content.ReadAsStringAsync();
        var list = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(2, list.GetArrayLength());
        var hjem = list[0];
        var sommerhus = list[1];
        Assert.Equal("Hjem", hjem.GetProperty("name").GetString());
        Assert.Equal("Normal", hjem.GetProperty("status").GetString());
        Assert.Equal("Observer", sommerhus.GetProperty("status").GetString());
        Assert.Equal("2026-11-06", sommerhus.GetProperty("latestComplete").GetProperty("date").GetString());
        Assert.True(sommerhus.GetProperty("latestComplete").GetProperty("kWh").GetDouble() < 2);
        Assert.Equal("2026-11-07", sommerhus.GetProperty("pendingDays")[0].GetString());
        Assert.Equal(14, sommerhus.GetProperty("sparkline").GetArrayLength());
        Assert.True(sommerhus.GetProperty("hasBaseline").GetBoolean());
        Assert.True(sommerhus.GetProperty("deviationPercent").GetDouble() < -50);
        Assert.Equal(32.1, sommerhus.GetProperty("dataAgeHours").GetDouble());
        Assert.Equal(JsonValueKind.String, sommerhus.GetProperty("lastSuccessfulSyncUtc").ValueKind);
        Assert.Equal(JsonValueKind.Number, hjem.GetProperty("sum7").ValueKind);
        Assert.Equal(JsonValueKind.Number, hjem.GetProperty("sum30").ValueKind);
        Assert.Equal("…0028", sommerhus.GetProperty("meteringPoint").GetString());
        Assert.DoesNotContain(FakeState.Sommerhus, raw);
        Assert.Equal("no-store", json.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task DailyConsumption_ReportsMissingDaysAsNull_NeverZero()
    {
        using var client = _app.ClientFor(AppFactory.Viewer);

        var json = await client.GetJsonAsync("/api/properties/sommerhus/consumption?from=2026-11-04&to=2026-11-09&resolution=day");

        var items = json.GetProperty("items");
        Assert.Equal(6, items.GetArrayLength());
        Assert.Equal("Complete", items[0].GetProperty("quality").GetString());
        Assert.Equal(JsonValueKind.Number, items[0].GetProperty("kWh").ValueKind);
        foreach (var index in new[] { 3, 4, 5 }) // 7/11 was fetched and empty; 8/11 and 9/11 have no row at all
        {
            Assert.Equal("Missing", items[index].GetProperty("quality").GetString());
            Assert.Equal(JsonValueKind.Null, items[index].GetProperty("kWh").ValueKind);
        }

        Assert.Equal(3, json.GetProperty("completeDays").GetInt32());
        Assert.Equal(JsonValueKind.Number, json.GetProperty("median").ValueKind);
    }

    [Fact]
    public async Task HourlyConsumption_ReturnsTheDaysHours()
    {
        using var client = _app.ClientFor(AppFactory.Viewer);

        var day = await client.GetJsonAsync("/api/properties/hjem/consumption?from=2026-11-05&to=2026-11-05&resolution=hour");
        var dst = await client.GetJsonAsync("/api/properties/hjem/consumption?from=2026-10-25&to=2026-10-25&resolution=hour");
        var empty = await client.GetJsonAsync("/api/properties/hjem/consumption?from=2026-11-07&to=2026-11-07&resolution=hour");

        Assert.Equal(24, day.GetProperty("items").GetArrayLength());
        Assert.Equal("2026-11-04T23:00:00+00:00", day.GetProperty("items")[0].GetProperty("startUtc").GetString());
        Assert.Equal(25, dst.GetProperty("items").GetArrayLength());
        Assert.Equal(0, empty.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("/api/properties/sommerhus/consumption?from=2026-11-07&to=2026-11-01")]
    [InlineData("/api/properties/sommerhus/consumption?from=x&to=2026-11-01")]
    [InlineData("/api/properties/sommerhus/consumption?from=2020-01-01&to=2026-11-01")]
    [InlineData("/api/properties/sommerhus/consumption?from=2026-01-01&to=2026-11-01&resolution=hour")]
    public async Task Consumption_RejectsBadRanges(string url)
    {
        using var client = _app.ClientFor(AppFactory.Viewer);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task UnknownProperty_Is404()
    {
        using var client = _app.ClientFor(AppFactory.Viewer);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/properties/nope/consumption?from=2026-11-01&to=2026-11-02")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/properties/nope/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/properties/nope/rules")).StatusCode);
    }

    [Fact]
    public async Task Alerts_ListWithReasonAndBasis_AndCanBeAcknowledged()
    {
        using var client = _app.ClientFor(AppFactory.Viewer);

        var open = await client.GetJsonAsync("/api/alerts?state=open");
        var alert = Assert.Single(open.EnumerateArray());
        Assert.Equal("Sommerhus", alert.GetProperty("propertyName").GetString());
        Assert.Equal("Observer", alert.GetProperty("label").GetString());
        Assert.Contains("under 50 % af medianen", alert.GetProperty("explanation").GetString());
        Assert.Equal(JsonValueKind.Number, alert.GetProperty("observed").ValueKind);
        Assert.Equal(JsonValueKind.Number, alert.GetProperty("baseline").ValueKind);
        Assert.Equal(JsonValueKind.Number, alert.GetProperty("threshold").ValueKind);
        Assert.Equal(JsonValueKind.Null, alert.GetProperty("acknowledgedAtUtc").ValueKind);
        Assert.Equal(0, (await client.GetJsonAsync("/api/alerts?state=closed")).GetArrayLength());

        var id = alert.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/alerts/{id}/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/alerts/sommerhus~LowConsumption~2020-01-01/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/alerts/garbage/acknowledge", null)).StatusCode);

        var after = await client.GetJsonAsync("/api/properties/sommerhus/alerts");
        Assert.Equal(JsonValueKind.String, after[0].GetProperty("acknowledgedAtUtc").ValueKind);

        // Acknowledging does not make a later sync raise or announce the event again.
        await _app.SyncAsync();
        Assert.Single(await _app.EventsAsync());
        Assert.Single(_app.Telegrams);
    }

    [Fact]
    public async Task Rules_CanBeReadAndChangedByAdmin_AndInvalidValuesAreRefused()
    {
        using var admin = _app.ClientFor(AppFactory.Admin);

        var rules = await admin.GetJsonAsync("/api/properties/hjem/rules");
        Assert.Equal(3, rules.GetArrayLength());
        Assert.Equal(50, rules[0].GetProperty("params").GetProperty("thresholdPercent").GetDouble());
        Assert.Equal(200, rules[1].GetProperty("params").GetProperty("thresholdPercent").GetDouble());
        Assert.Equal(72, rules[2].GetProperty("params").GetProperty("missingHours").GetInt32());

        var update = new { enabled = false, @params = new { thresholdPercent = 35, minBaselineKWh = 2, baselineDays = 14, minBaselineDays = 7, escalateAfterDays = 3, missingHours = 72, consecutiveFailures = 3 } };
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/properties/hjem/rules/LowConsumption", update)).StatusCode);

        var changed = (await admin.GetJsonAsync("/api/properties/hjem/rules"))[0];
        Assert.False(changed.GetProperty("enabled").GetBoolean());
        Assert.Equal(35, changed.GetProperty("params").GetProperty("thresholdPercent").GetDouble());
        Assert.Equal(3, changed.GetProperty("params").GetProperty("escalateAfterDays").GetInt32());

        var invalid = new { enabled = true, @params = new { thresholdPercent = -5, minBaselineKWh = 2, baselineDays = 14, minBaselineDays = 7, escalateAfterDays = 3, missingHours = 72, consecutiveFailures = 3 } };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/properties/hjem/rules/LowConsumption", invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/properties/hjem/rules/Nope", update)).StatusCode);
    }

    [Fact]
    public async Task Health_ShowsSyncAndTokenState_WithoutSecrets()
    {
        using var client = _app.ClientFor(AppFactory.Viewer);

        var response = await client.GetAsync("/api/system/health");
        var raw = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(raw).RootElement;

        Assert.Equal("Succeeded", json.GetProperty("lastSync").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, json.GetProperty("lastSuccessfulSyncUtc").ValueKind);
        Assert.True(json.GetProperty("token").GetProperty("configured").GetBoolean());
        Assert.Equal("unknown", json.GetProperty("token").GetProperty("state").GetString()); // the test token is not a JWT
        Assert.True(json.GetProperty("notifications").GetProperty("configured").GetBoolean());
        Assert.Equal(0, json.GetProperty("notifications").GetProperty("pending").GetInt32());
        Assert.DoesNotContain(AppFactory.RefreshToken, raw);
        Assert.DoesNotContain(AppFactory.BotToken, raw);
        Assert.DoesNotContain(FakeState.Sommerhus, raw);
    }

    [Fact]
    public async Task TestMessage_IsDelivered_AndAFailureIsReported()
    {
        using var admin = _app.ClientFor(AppFactory.Admin);

        var ok = await admin.PostAsync("/api/admin/notifications/test", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains(_app.Telegrams, t => t.Contains("testbesked", StringComparison.Ordinal));
        Assert.Equal(AppFactory.ChatId, _app.Fake.TelegramMessages[^1].ChatId);

        _app.Fake.TelegramDown = true;
        var failed = await admin.PostAsync("/api/admin/notifications/test", null);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var body = await failed.Content.ReadAsStringAsync();
        Assert.Contains("Telegram answered 500", body);
        Assert.DoesNotContain(AppFactory.BotToken, body);
    }

    [Fact]
    public async Task NotificationSettings_CanBeChanged_AndTurnTheDailyReportOff()
    {
        using var admin = _app.ClientFor(AppFactory.Admin);

        var initial = await admin.GetJsonAsync("/api/admin/settings/notifications");
        Assert.True(initial.GetProperty("dailyReportEnabled").GetBoolean());
        Assert.Equal(AppFactory.ChatId, initial.GetProperty("recipients")[0].GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/settings/notifications", new { dailyReportEnabled = true, recipients = new[] { "not a chat id" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/settings/notifications", new { dailyReportEnabled = false, recipients = new[] { "2000", "3000" } })).StatusCode);

        Assert.False(await _app.Get<EnergyMonitor.Web.Jobs.ReportService>().EnqueueDailyAsync(default));
        await admin.PostAsync("/api/admin/notifications/test", null);
        Assert.Equal(["2000", "3000"], _app.Fake.TelegramMessages.TakeLast(2).Select(m => m.ChatId));
    }

    [Fact]
    public async Task ReportPreview_NamesIncompleteDays()
    {
        using var admin = _app.ClientFor(AppFactory.Admin);

        var text = (await admin.GetJsonAsync("/api/admin/report")).GetProperty("text").GetString()!;

        Assert.Contains("Lavere end normalt (median", text);
        Assert.Contains("Observer.", text);
        Assert.Contains("Bemærk: Data for 7/11 er endnu ikke komplette", text);
    }

    [Fact]
    public async Task MeteringPoints_AreListedForAdmin()
    {
        using var admin = _app.ClientFor(AppFactory.Admin);

        var points = await admin.GetJsonAsync("/api/admin/metering-points");

        Assert.Equal(2, points.GetArrayLength());
        Assert.Equal("Klitvejen 3 4573 Højby", points[1].GetProperty("address").GetString());
        Assert.Equal("…0028", points[1].GetProperty("masked").GetString());
    }

    [Fact]
    public async Task Property_CanBeCreatedRenamedAndPaused_AndChangingTheMeterClearsItsHistory()
    {
        using var admin = _app.ClientFor(AppFactory.Admin);

        var created = await admin.PostAsJsonAsync("/api/admin/properties", new { name = "Garage Øst", meteringPointId = (string?)null, monitoringEnabled = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(await _app.Get<IPropertyStore>().GetAsync("garage-oest", default));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/properties", new { name = "Garage Øst", monitoringEnabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/properties", new { name = "X", meteringPointId = "123", monitoringEnabled = true })).StatusCode);

        // Renaming and pausing keeps the meter (an empty id means "unchanged") and the history.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/properties/sommerhus", new { name = "Sommerhuset", meteringPointId = "", monitoringEnabled = false })).StatusCode);
        var renamed = await _app.Get<IPropertyStore>().GetAsync("sommerhus", default);
        Assert.Equal(("Sommerhuset", FakeState.Sommerhus, false), (renamed!.Name, renamed.MeteringPointId, renamed.MonitoringEnabled));
        Assert.NotEmpty(await _app.Get<IConsumptionStore>().GetDailyAsync("sommerhus", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), default));

        // Linking another meter drops the old meter's consumption and alarms.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/properties/sommerhus", new { name = "Sommerhuset", meteringPointId = FakeState.Hjem, monitoringEnabled = true })).StatusCode);
        Assert.Empty(await _app.Get<IConsumptionStore>().GetDailyAsync("sommerhus", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), default));
        Assert.Empty(await _app.EventsAsync());
        Assert.NotEmpty(await _app.Get<IConsumptionStore>().GetDailyAsync("hjem", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), default));

        await _app.SyncAsync(); // and the next sync backfills the new meter
        Assert.Equal(90, (await _app.Get<IConsumptionStore>().GetDailyAsync("sommerhus", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), default)).Count);
    }

    [Fact]
    public async Task Export_ContainsConfigurationAndHistory_AndDeleteRemovesAPropertysData()
    {
        using var admin = _app.ClientFor(AppFactory.Admin);

        var export = await admin.GetJsonAsync("/api/admin/export");
        var sommerhus = export.GetProperty("properties")[1];
        Assert.Equal("Sommerhus", sommerhus.GetProperty("property").GetProperty("name").GetString());
        Assert.StartsWith("…", sommerhus.GetProperty("property").GetProperty("meteringPointId").GetString());
        Assert.Equal(90, sommerhus.GetProperty("days").GetArrayLength());
        Assert.Equal((89 * 24) + 1, sommerhus.GetProperty("intervals").GetArrayLength());
        Assert.Equal(3, sommerhus.GetProperty("rules").GetArrayLength());
        Assert.Equal(1, sommerhus.GetProperty("events").GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/properties/sommerhus/data")).StatusCode);

        var after = (await admin.GetJsonAsync("/api/admin/export")).GetProperty("properties");
        Assert.Equal(0, after[1].GetProperty("days").GetArrayLength());
        Assert.Equal(0, after[1].GetProperty("intervals").GetArrayLength());
        Assert.Equal(90, after[0].GetProperty("days").GetArrayLength());
    }

    [Fact]
    public async Task ManualSync_IsAccepted_AndRunsInTheBackground()
    {
        using var admin = _app.ClientFor(AppFactory.Admin);
        var runs = _app.Get<ISyncRunStore>();

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/admin/sync", null)).StatusCode);

        for (var i = 0; i < 100 && (await runs.GetRecentAsync("eloverblik", 10, default)).Count(r => r.Status == SyncStatus.Succeeded) < 2; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, (await runs.GetRecentAsync("eloverblik", 10, default)).Count(r => r.Status == SyncStatus.Succeeded));
    }

    [Fact]
    public async Task SecurityHeaders_AreSet_AndHealthProbesAreOpenButEmpty()
    {
        using var client = _app.ClientFor(null);

        var alive = await client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Equal("Healthy", await alive.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", alive.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'self'", alive.Headers.GetValues("Content-Security-Policy").Single());
    }
}
