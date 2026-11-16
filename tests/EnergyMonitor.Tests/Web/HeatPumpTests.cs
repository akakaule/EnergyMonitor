using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyMonitor.Core.Model;
using EnergyMonitor.FakeUpstreams;
using EnergyMonitor.Web.HeatPump;
using EnergyMonitor.Web.Jobs;
using EnergyMonitor.Web.Storage;
using Microsoft.Extensions.Time.Testing;

namespace EnergyMonitor.Tests.Web;

public class HeatPumpTests : IAsyncLifetime
{
    private const string Pods = "/api/heatpump/sensibo/users/me/pods";
    private const string AcStates = "/api/heatpump/sensibo/pods/" + FakeSensibo.DeviceId + "/acStates";

    private readonly AppFactory _app = new(heatpump: true);

    public async ValueTask InitializeAsync() => await _app.SeedPropertiesAsync();

    public ValueTask DisposeAsync()
    {
        _app.Dispose();
        return ValueTask.CompletedTask;
    }

    private int PodCalls => _app.Fake.Locked(() => _app.Fake.Sensibo.PodCalls);

    private async Task<IReadOnlyList<AlertEvent>> HeatPumpEventsAsync(string propertyId = "sommerhus") =>
        [.. (await _app.EventsAsync(propertyId)).Where(e => e.RuleType is RuleType.IndoorTooCold or RuleType.HeatPumpOffline or RuleType.SensiboFailure)];

    private async Task<JsonElement> HistoryAsync(int days)
    {
        using var client = _app.ClientFor(AppFactory.Viewer);
        return await client.GetJsonAsync($"/api/heatpump/history/{FakeSensibo.DeviceId}?days={days}");
    }

    [Fact]
    public async Task Collection_RecordsSensibosLast24Hours_AndARepeatAddsOnlyNewReadings()
    {
        await _app.LinkHeatPumpAsync();

        await _app.CollectHeatPumpAsync();
        var first = (await HistoryAsync(1)).GetProperty("temperature").GetArrayLength();
        await _app.CollectHeatPumpAsync(TimeSpan.FromMinutes(15));
        var second = await HistoryAsync(1);

        Assert.Equal(288, first);
        Assert.InRange(second.GetProperty("temperature").GetArrayLength(), 288, 291);
        Assert.Equal(second.GetProperty("temperature").GetArrayLength(), second.GetProperty("humidity").GetArrayLength());
        var last = second.GetProperty("temperature").EnumerateArray().Last();
        Assert.Equal(10.5, last.GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task History_OverLongRanges_IsAveragedIntoBuckets_AndValidatesItsInput()
    {
        await _app.LinkHeatPumpAsync();
        for (var i = 0; i < 7; i++)
        {
            await _app.CollectHeatPumpAsync(TimeSpan.FromHours(24));
        }

        var week = (await HistoryAsync(7)).GetProperty("temperature");
        Assert.InRange(week.GetArrayLength(), 600, 721);

        using var client = _app.ClientFor(AppFactory.Viewer);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/heatpump/history/{FakeSensibo.DeviceId}?days=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/heatpump/history/{FakeSensibo.DeviceId}?days=367")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/heatpump/history/SomeoneElse?days=1")).StatusCode);
    }

    [Fact]
    public async Task Scheduler_CollectsOncePerInterval_AndARestartDoesNotRepeatTheSlot()
    {
        var scheduler = _app.Get<SchedulerService>();

        await scheduler.TickAsync(default);
        _app.Time.Advance(TimeSpan.FromMinutes(5));
        await scheduler.TickAsync(default);
        Assert.Equal(1, PodCalls);

        _app.Time.Advance(TimeSpan.FromMinutes(10));
        await scheduler.TickAsync(default);
        Assert.Equal(2, PodCalls);

        using var restarted = new AppFactory(_app.Backend, _app.Fake, _app.Time, heatpump: true);
        await restarted.Get<SchedulerService>().TickAsync(default);
        Assert.Equal(2, PodCalls);
    }

    [Fact]
    public async Task Cold_SendsOneTelegram_ThenAnAlarmAfterTwoHours_AndClosesWhenWarmAgain()
    {
        await _app.LinkHeatPumpAsync();
        _app.Fake.ApplyScenario("cold");

        await _app.CollectHeatPumpAsync();
        await _app.CollectHeatPumpAsync(TimeSpan.FromMinutes(15));
        var observer = Assert.Single(_app.Telegrams);
        Assert.StartsWith("Energy Monitor · Sommerhus\nObserver: Indetemperaturen er 3,5 °C", observer, StringComparison.Ordinal);

        await _app.CollectHeatPumpAsync(TimeSpan.FromMinutes(105));
        Assert.Equal(2, _app.Telegrams.Count);
        Assert.Contains("Alarm: Indetemperaturen er 3,5 °C", _app.Telegrams[1], StringComparison.Ordinal);

        _app.Fake.ApplyScenario("normal");
        await _app.CollectHeatPumpAsync(TimeSpan.FromMinutes(15));
        var e = Assert.Single(await HeatPumpEventsAsync());
        Assert.Equal((RuleType.IndoorTooCold, AlertSeverity.Alarm, AlertState.Closed), (e.RuleType, e.Severity, e.State));
        Assert.Equal(2, _app.Telegrams.Count);
    }

    [Fact]
    public async Task Offline_RaisesAnAlarmMessage_AndTheDashboardShowsAlarm()
    {
        await _app.LinkHeatPumpAsync();
        _app.Fake.ApplyScenario("offline");

        await _app.CollectHeatPumpAsync();

        Assert.Contains(_app.Telegrams, t => t.Contains("Alarm: Varmepumpen har ikke haft forbindelse til Sensibo i over 3 timer", StringComparison.Ordinal));
        using var client = _app.ClientFor(AppFactory.Viewer);
        var properties = await client.GetJsonAsync("/api/properties");
        var sommerhus = properties.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "sommerhus");
        Assert.Equal("Alarm", sommerhus.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SensiboDown_ThreeCollectionsInARow_RaisesOneTechnicalAlarm_AndNoFalseHouseAlarms()
    {
        await _app.LinkHeatPumpAsync();
        await _app.CollectHeatPumpAsync();
        _app.Fake.ApplyScenario("sensibo-down");

        for (var i = 0; i < 4; i++)
        {
            await _app.CollectHeatPumpAsync(TimeSpan.FromMinutes(15));
        }

        var technical = Assert.Single(await HeatPumpEventsAsync(AlertEvent.SystemPropertyId));
        Assert.Equal((RuleType.SensiboFailure, AlertState.Open), (technical.RuleType, technical.State));
        Assert.Empty(await HeatPumpEventsAsync());
        var message = Assert.Single(_app.Telegrams);
        Assert.Contains("Teknisk alarm: 3 hentninger fra Sensibo i træk er fejlet (seneste fejl: sensibo_error)", message, StringComparison.Ordinal);

        using var client = _app.ClientFor(AppFactory.Viewer);
        var status = await client.GetJsonAsync("/api/heatpump");
        Assert.Equal("sensibo_error", status.GetProperty("lastError").GetString());
        Assert.Equal(10.5, status.GetProperty("devices")[0].GetProperty("temperature").GetDouble());

        _app.Fake.ApplyScenario("normal");
        await _app.CollectHeatPumpAsync(TimeSpan.FromMinutes(15));
        Assert.Equal(AlertState.Closed, Assert.Single(await HeatPumpEventsAsync(AlertEvent.SystemPropertyId)).State);
    }

    [Fact]
    public async Task ARejectedKey_RaisesTheTechnicalAlarmAtOnce()
    {
        _app.Fake.ApplyScenario("sensibo-key-rejected");

        await _app.CollectHeatPumpAsync();

        Assert.Contains("Sensibo afviste API-nøglen", Assert.Single(_app.Telegrams), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnlinkedHeatPump_RaisesNoHouseAlarms_AndOnlyALinkedPropertyCarriesItsRules()
    {
        using var client = _app.ClientFor(AppFactory.Viewer);
        _app.Fake.ApplyScenario("cold");

        await _app.CollectHeatPumpAsync();
        Assert.Empty(await HeatPumpEventsAsync());
        Assert.DoesNotContain("IndoorTooCold", await client.GetStringAsync("/api/properties/sommerhus/rules"), StringComparison.Ordinal);

        await _app.LinkHeatPumpAsync();
        var rules = await client.GetStringAsync("/api/properties/sommerhus/rules");
        Assert.Contains("IndoorTooCold", rules, StringComparison.Ordinal);
        Assert.Contains("\"minTemperatureC\":5", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("IndoorTooCold", await client.GetStringAsync("/api/properties/hjem/rules"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ColdRule_CanBeTunedAndDisabled_WithValidatedParams()
    {
        await _app.LinkHeatPumpAsync();
        using var admin = _app.ClientFor(AppFactory.Admin);
        var p = RuleParams.DefaultFor(RuleType.IndoorTooCold);

        var invalid = await admin.PutAsJsonAsync("/api/properties/sommerhus/rules/IndoorTooCold", new { enabled = true, @params = p with { MinTemperatureC = 40 } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var saved = await admin.PutAsJsonAsync("/api/properties/sommerhus/rules/IndoorTooCold", new { enabled = true, @params = p with { MinTemperatureC = 12 } });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        await _app.CollectHeatPumpAsync();
        Assert.Equal(12, Assert.Single(await HeatPumpEventsAsync()).Threshold);
    }

    [Fact]
    public async Task Status_ListsDevicesWithTheirLink_AndLinksAreValidated()
    {
        await _app.CollectHeatPumpAsync();
        using var admin = _app.ClientFor(AppFactory.Admin);

        var unknown = await admin.PutAsJsonAsync("/api/admin/heatpump/links", new { links = new[] { new { deviceId = FakeSensibo.DeviceId, propertyId = "slottet" } } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("unknown_property", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var linked = await admin.PutAsJsonAsync("/api/admin/heatpump/links", new { links = new[] { new { deviceId = FakeSensibo.DeviceId, propertyId = "sommerhus" } } });
        Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);

        var status = await admin.GetJsonAsync("/api/heatpump");
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.Equal(15, status.GetProperty("intervalMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastError").ValueKind);
        Assert.Equal(JsonValueKind.String, status.GetProperty("oldestUtc").ValueKind);
        var device = Assert.Single(status.GetProperty("devices").EnumerateArray());
        Assert.Equal((FakeSensibo.DeviceId, "Stue", "sommerhus"), (device.GetProperty("id").GetString(), device.GetProperty("name").GetString(), device.GetProperty("propertyId").GetString()));
        Assert.Equal((10.5, 62.0, true, "heat"), (device.GetProperty("temperature").GetDouble(), device.GetProperty("humidity").GetDouble(), device.GetProperty("on").GetBoolean(), device.GetProperty("mode").GetString()));

        var cleared = await admin.PutAsJsonAsync("/api/admin/heatpump/links", new { links = new[] { new { deviceId = FakeSensibo.DeviceId, propertyId = (string?)null } } });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await admin.GetJsonAsync("/api/heatpump")).GetProperty("devices")[0].GetProperty("propertyId").ValueKind);
    }

    [Fact]
    public async Task OnePropertyTakesOneHeatPump()
    {
        using var admin = _app.ClientFor(AppFactory.Admin);

        var response = await admin.PutAsJsonAsync("/api/admin/heatpump/links", new
        {
            links = new[] { new { deviceId = FakeSensibo.DeviceId, propertyId = "sommerhus" }, new { deviceId = "Pod2Sove", propertyId = "sommerhus" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("property_taken", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnlinkingThePump_ClosesItsAlarm_SoTheHouseDoesNotStayInAlarm()
    {
        await _app.LinkHeatPumpAsync();
        _app.Fake.ApplyScenario("offline");
        await _app.CollectHeatPumpAsync();
        Assert.Equal(AlertState.Open, Assert.Single(await HeatPumpEventsAsync()).State);

        await _app.Get<HeatPumpStateService>().SetLinksAsync([], default);
        await _app.CollectHeatPumpAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(AlertState.Closed, Assert.Single(await HeatPumpEventsAsync()).State);
    }

    [Fact]
    public async Task ARoomSensor_IsWhatThePageShowsAndTheAlarmsJudge()
    {
        await _app.LinkHeatPumpAsync();
        _app.Fake.Locked(() => _app.Fake.Sensibo.RoomSensorTemperature = 3.0);

        await _app.CollectHeatPumpAsync();

        var e = Assert.Single(await HeatPumpEventsAsync());
        Assert.Equal((RuleType.IndoorTooCold, 3.0), (e.RuleType, e.Observed));
        using var viewer = _app.ClientFor(AppFactory.Viewer);
        Assert.Equal(3.0, (await viewer.GetJsonAsync("/api/heatpump")).GetProperty("devices")[0].GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task AStorageFailure_IsCountedAsAFailedCollection_AndTheTickStillDelivers()
    {
        using var app = new AppFactory(new FailingTable("heatpump", new InMemoryTableBackend()), heatpump: true);
        await app.SeedPropertiesAsync();

        await app.Get<SchedulerService>().TickAsync(default);

        using var viewer = app.ClientFor(AppFactory.Viewer);
        Assert.Equal("internal_error", (await viewer.GetJsonAsync("/api/heatpump")).GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task CollectNow_RunsInTheBackground()
    {
        using var admin = _app.ClientFor(AppFactory.Admin);

        var response = await admin.PostAsync("/api/admin/heatpump/collect", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        for (var i = 0; i < 100 && PodCalls == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, PodCalls);
    }

    [Fact]
    public async Task Proxy_ForwardsWithTheServersKey_ReplacingAnyClientKey()
    {
        using var viewer = _app.ClientFor(AppFactory.Viewer);

        var response = await viewer.GetAsync(Pods + "?fields=id,room&apiKey=stolen");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(FakeSensibo.DeviceId, body.GetProperty("result")[0].GetProperty("id").GetString());
        var query = Uri.UnescapeDataString(_app.Fake.Locked(() => _app.Fake.Sensibo.Queries.Last()));
        Assert.Contains("fields=id,room", query, StringComparison.Ordinal);
        Assert.Contains("apiKey=" + FakeSensibo.ApiKey, query, StringComparison.Ordinal);
        Assert.DoesNotContain("stolen", query, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSensibo.ApiKey, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("users/me")]
    [InlineData("pods/Pod1Stue/schedules")]
    [InlineData("users/me/pods/extra")]
    [InlineData("pods/Pod1Stue%2F..%2F..%2Fusers")]
    public async Task Proxy_RefusesPathsOutsideTheAllowlist_WithoutCallingSensibo(string path)
    {
        using var viewer = _app.ClientFor(AppFactory.Viewer);

        var response = await viewer.GetAsync("/api/heatpump/sensibo/" + path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_app.Fake.Locked(() => _app.Fake.Sensibo.Queries.ToList()));
    }

    [Fact]
    public async Task Proxy_Writes_ChangeTheHeatPump_AndNeedAnAdmin()
    {
        using var viewer = _app.ClientFor(AppFactory.Viewer);
        using var admin = _app.ClientFor(AppFactory.Admin);
        var change = new { acState = new { on = true, mode = "heat", targetTemperature = 12, temperatureUnit = "C" } };

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(AcStates, change)).StatusCode);
        var response = await admin.PostAsJsonAsync(AcStates, change);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(12, _app.Fake.Locked(() => _app.Fake.Sensibo.AcState["targetTemperature"]!.GetValue<int>()));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/heatpump/sensibo/pods/{FakeSensibo.DeviceId}/smartmode", new { enabled = true })).StatusCode);
        Assert.True(_app.Fake.Locked(() => _app.Fake.Sensibo.SmartModeEnabled));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/heatpump/sensibo/pods/{FakeSensibo.DeviceId}/timer/", new { minutesFromNow = 60, acState = new { on = false } })).StatusCode);
        // Sensibo's empty success becomes a 204, which the browser does not try to parse as JSON.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/heatpump/sensibo/pods/{FakeSensibo.DeviceId}/timer/")).StatusCode);
        Assert.Null(_app.Fake.Locked(() => _app.Fake.Sensibo.Timer));
    }

    [Theory]
    [InlineData("sensibo-key-rejected", HttpStatusCode.BadGateway, "sensibo_key_rejected")]
    [InlineData("sensibo-down", HttpStatusCode.BadGateway, "sensibo_error")]
    public async Task Proxy_SensiboFailures_BecomeCodes_AndNeverA401ThatWouldSignTheUserOut(string scenario, HttpStatusCode status, string code)
    {
        using var viewer = _app.ClientFor(AppFactory.Viewer);
        _app.Fake.ApplyScenario(scenario);

        var response = await viewer.GetAsync(Pods);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task WithoutAKey_TheProxySaysSo_AndCollectionDoesNothing()
    {
        using var unconfigured = _app.WithWebHostBuilder(b => b.UseSetting("Sensibo:ApiKey", ""));
        using var viewer = unconfigured.CreateClient();
        viewer.DefaultRequestHeaders.Add(EnergyMonitor.Web.Auth.DevAuthHandler.Header, AppFactory.Viewer);

        var response = await viewer.GetAsync(Pods);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("sensibo_not_configured", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False((await viewer.GetJsonAsync("/api/heatpump")).GetProperty("configured").GetBoolean());
        Assert.Equal(0, PodCalls);
    }

    [Fact]
    public async Task DailyReport_HasALineForTheLinkedHeatPump()
    {
        await _app.LinkHeatPumpAsync();
        await _app.CollectHeatPumpAsync();

        var report = await _app.Get<ReportService>().ComposeAsync(default);

        Assert.Contains("Sommerhus · varmepumpe: 10,5 °C inde, 62 % fugt (målt 8/11 kl. 08:04). Tændt: varme 10 °C.", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSensiboKey_NeverReachesTheLogs()
    {
        await _app.LinkHeatPumpAsync();
        await _app.CollectHeatPumpAsync();
        using var admin = _app.ClientFor(AppFactory.Admin);
        await admin.GetAsync(Pods + "?fields=id");
        await admin.PostAsJsonAsync(AcStates, new { acState = new { on = false } });
        _app.Fake.ApplyScenario("sensibo-down");
        await _app.CollectHeatPumpAsync();
        await admin.GetAsync(Pods);
        _app.Fake.ApplyScenario("normal");
        _app.Fake.ApplyScenario("sensibo-key-rejected");
        await _app.CollectHeatPumpAsync();

        var logs = _app.Logs.Text;
        Assert.Contains("Heat pump collection failed", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSensibo.ApiKey, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_TheModuleIsAbsent()
    {
        using var app = new AppFactory();
        using var viewer = app.ClientFor(AppFactory.Viewer);

        Assert.False((await viewer.GetJsonAsync("/api/features")).GetProperty("heatpump").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/heatpump")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Pods)).StatusCode);
        await app.Get<SchedulerService>().TickAsync(default);
        Assert.Equal(0, app.Fake.Locked(() => app.Fake.Sensibo.PodCalls));
    }

    [Fact]
    public async Task Mcp_HeatPumpStatus_IsReadOnlyAndShowsTheLinkedHouse()
    {
        using var app = new AppFactory(mcp: true, local: true, heatpump: true);
        await app.SeedPropertiesAsync();
        await app.LinkHeatPumpAsync();
        await app.CollectHeatPumpAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        async Task<JsonElement> CallAsync(object request)
        {
            var response = await client.PostAsJsonAsync("/mcp", request);
            var text = await response.Content.ReadAsStringAsync();
            if (text.StartsWith("event:", StringComparison.Ordinal))
                text = text.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
            return JsonDocument.Parse(text).RootElement.GetProperty("result");
        }

        var tools = (await CallAsync(new { jsonrpc = "2.0", id = 1, method = "tools/list" })).GetProperty("tools").EnumerateArray().ToList();
        var tool = Assert.Single(tools, t => t.GetProperty("name").GetString() == "get_heatpump_status");
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.DoesNotContain(tools, t => t.GetProperty("name").GetString()!.Contains("set", StringComparison.Ordinal));

        var result = await CallAsync(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "get_heatpump_status", arguments = new { } } });
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("\"propertyName\":\"Sommerhus\"", text, StringComparison.Ordinal);
        Assert.Contains("\"temperature\":10.5", text, StringComparison.Ordinal);
    }
}

/// <summary>A backend whose writes to one table fail, as a full disk or a locked file would.</summary>
internal sealed class FailingTable(string table, ITableBackend inner) : ITableBackend
{
    public Task<Row?> GetAsync(string t, string pk, string rk, CancellationToken ct) => inner.GetAsync(t, pk, rk, ct);

    public Task UpsertAsync(string t, IReadOnlyCollection<Row> rows, CancellationToken ct) =>
        t == table ? throw new IOException("disk full") : inner.UpsertAsync(t, rows, ct);

    public Task<bool> TryAddAsync(string t, Row row, CancellationToken ct) => inner.TryAddAsync(t, row, ct);

    public Task DeleteAsync(string t, IReadOnlyCollection<Row> rows, CancellationToken ct) => inner.DeleteAsync(t, rows, ct);

    public Task<IReadOnlyList<Row>> QueryAsync(string t, string pk, string? rkFrom, string? rkTo, int? take, CancellationToken ct) =>
        inner.QueryAsync(t, pk, rkFrom, rkTo, take, ct);

    public Task<IReadOnlyList<Row>> QueryPartitionsAsync(string t, string pkFrom, string pkTo, CancellationToken ct) =>
        inner.QueryPartitionsAsync(t, pkFrom, pkTo, ct);
}

public sealed class HeatPumpStoreTests
{
    [Fact]
    public void AnHourOfReadings_StaysWellBelowTheTablePropertyLimit()
    {
        var hour = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 60).Select(i => new HeatPumpPoint(hour.AddMinutes(i).ToUnixTimeSeconds(), -12.25, 99.5)).ToList();

        Assert.True(StoreJson.Write(new HeatPumpHour(points)).Length < 4000);
    }
}
