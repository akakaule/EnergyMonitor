using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Web.Jobs;
using EnergyMonitor.Web.Pricing;

namespace EnergyMonitor.Tests.Web;

/// <summary>
/// Hourly prices through the API, the daily report, the afternoon price slot and the message about tomorrow.
/// "Today" is Sunday 8 November 2026, 08:05; the fake publishes tomorrow's day-ahead prices at 13:00.
/// </summary>
public sealed class PriceOutlookTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);
    private static readonly DateOnly Tomorrow = new(2026, 11, 9);
    private static readonly ElectricityAgreement Spot = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null, "DK1", "Spot", null, 0.1m, 2m, 60, 0.25m);

    private static async Task<AppFactory> StartAsync(bool pricing = true, bool mcp = false, bool local = false)
    {
        var app = new AppFactory(pricing: pricing, mcp: mcp, local: local);
        await app.SeedPropertiesAsync();
        foreach (var id in new[] { "hjem", "sommerhus" })
        {
            await app.Get<PricingStore>().SaveConfigurationAsync(id, new([Spot], [], [], false), default);
        }

        return app;
    }

    /// <summary>Moves the clock to a Danish time of day today and lets the scheduler run what is due.</summary>
    private static async Task TickAtAsync(AppFactory app, int hour, int minute)
    {
        var target = new DateTimeOffset(2026, 11, 8, hour - 1, minute, 0, TimeSpan.Zero);
        app.Time.Advance(target - app.Time.GetUtcNow());
        await app.Get<SchedulerService>().TickAsync(default);
        await app.Get<SchedulerService>().TickAsync(default);
    }

    [Fact]
    public async Task Prices_GiveTodayHourByHour_AndTomorrowAsPendingUntilPublished()
    {
        using var app = await StartAsync();
        await app.SyncAsync();
        using var client = app.ClientFor(AppFactory.Viewer);

        var view = await client.GetJsonAsync("/api/energy/hjem/prices?from=2026-11-08&to=2026-11-09");

        Assert.Equal("DK1", view.GetProperty("area").GetString());
        Assert.Equal("Spot", view.GetProperty("kind").GetString());
        Assert.Equal(3, view.GetProperty("windowHours").GetInt32());
        Assert.Equal(["charges_missing"], view.GetProperty("assumptions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(JsonValueKind.String, view.GetProperty("lastPriceSyncUtc").ValueKind);
        var days = view.GetProperty("days").EnumerateArray().ToArray();
        Assert.Equal(("2026-11-08", "Complete"), (days[0].GetProperty("date").GetString(), days[0].GetProperty("status").GetString()));
        Assert.Equal(("2026-11-09", "Pending"), (days[1].GetProperty("date").GetString(), days[1].GetProperty("status").GetString()));

        var hours = days[0].GetProperty("hours").EnumerateArray().ToArray();
        Assert.Equal(24, hours.Length);
        var seven = hours[7];
        Assert.Equal("2026-11-08T06:00:00+00:00", seven.GetProperty("startUtc").GetString());
        Assert.Equal(0.35m, seven.GetProperty("spot").GetDecimal()); // the fake's cheapest hour, 350 DKK/MWh
        Assert.Equal((0.35m + 0.1m) * 1.25m, seven.GetProperty("total").GetDecimal());
        // It is 08:05: the periods are looked for among the hours from 08 on, where the price only rises until 19.
        Assert.Equal("2026-11-08T07:00:00+00:00", days[0].GetProperty("cheapest").GetProperty("fromUtc").GetString());
        Assert.Equal("2026-11-08T17:00:00+00:00", days[0].GetProperty("mostExpensive").GetProperty("fromUtc").GetString());
        Assert.Equal(24, days[1].GetProperty("hours").GetArrayLength());
        Assert.False(days[1].TryGetProperty("average", out var average) && average.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Prices_AreBounded_NeedSignIn_AndNeverGuessThePriceArea()
    {
        using var app = await StartAsync();
        await app.Get<PricingStore>().SaveConfigurationAsync("sommerhus", PricingConfiguration.Empty, default);
        using var viewer = app.ClientFor(AppFactory.Viewer);
        using var anonymous = app.ClientFor(null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/energy/hjem/prices?from=2026-11-08&to=2026-11-08")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/energy/hjem/prices?from=2026-11-04&to=2026-11-10")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/energy/hjem/prices?from=2026-11-03&to=2026-11-10")).StatusCode); // 8 days
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/energy/hjem/prices?from=2026-11-08&to=2026-11-11")).StatusCode); // 3 days ahead
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/energy/absent/prices?from=2026-11-08&to=2026-11-08")).StatusCode);

        var none = await viewer.GetJsonAsync("/api/energy/sommerhus/prices?from=2026-11-08&to=2026-11-09");
        Assert.False(none.TryGetProperty("area", out var area) && area.ValueKind != JsonValueKind.Null);
        Assert.Equal(["no_agreement"], none.GetProperty("assumptions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(0, none.GetProperty("days").GetArrayLength());
    }

    [Fact]
    public async Task Prices_AreNotServed_WhenPricingIsOff()
    {
        using var app = await StartAsync(pricing: false);
        using var client = app.ClientFor(AppFactory.Viewer);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/energy/hjem/prices?from=2026-11-08&to=2026-11-08")).StatusCode);
    }

    [Fact]
    public async Task DailyReport_HasOneLineForPropertiesThatShareTerms_AboutTheRestOfTheDay()
    {
        using var app = await StartAsync();
        await TickAtAsync(app, 8, 6);
        await TickAtAsync(app, 9, 1);

        var report = Assert.Single(app.Telegrams);
        var line = Assert.Single(report.Split('\n'), l => l.StartsWith("Elpris i dag", StringComparison.Ordinal));
        Assert.StartsWith("Elpris i dag (Hjem, Sommerhus, DK1): gns. 0,8", line);
        Assert.Contains(" kr./kWh. Resten af dagen: billigst kl. 09–12 (", line);
        Assert.Contains("), dyrest kl. 18–21 (", line);
    }

    [Fact]
    public async Task DailyReport_GivesEachPropertyItsOwnLine_WhenTermsDiffer_AndNoneWithoutPrices()
    {
        using var app = await StartAsync();
        await app.Get<PricingStore>().SaveConfigurationAsync("sommerhus", new([Spot with { SupplierMarkup = 0.5m }], [], [], false), default);
        Assert.DoesNotContain("Elpris", await app.Get<ReportService>().ComposeAsync(default)); // nothing fetched yet

        await app.SyncAsync();
        var report = await app.Get<ReportService>().ComposeAsync(default);

        Assert.Contains("Elpris i dag (Hjem, DK1): gns. 0,8", report);
        Assert.Contains("Elpris i dag (Sommerhus, DK1): gns. 1,3", report);
    }

    [Fact]
    public async Task AfternoonSlot_FetchesOnlyPrices_Once_AndAnOrdinaryTomorrowSendsNothing()
    {
        using var app = await StartAsync();
        await TickAtAsync(app, 8, 6);
        var (spotCalls, seriesCalls) = (app.Fake.SpotCalls, app.Fake.TimeSeriesCalls);

        await TickAtAsync(app, 13, 31);

        Assert.True(app.Fake.SpotCalls > spotCalls);
        Assert.Equal(seriesCalls, app.Fake.TimeSeriesCalls); // Eloverblik is not called
        var after = app.Fake.SpotCalls;
        await TickAtAsync(app, 13, 40);
        Assert.Equal(after, app.Fake.SpotCalls);
        using var client = app.ClientFor(AppFactory.Viewer);
        var tomorrow = (await client.GetJsonAsync("/api/energy/hjem/prices?from=2026-11-09&to=2026-11-09")).GetProperty("days")[0];
        Assert.Equal("Complete", tomorrow.GetProperty("status").GetString());
        // The daily report was caught up on the way; an ordinary tomorrow gets no message of its own.
        Assert.DoesNotContain(app.Telegrams, t => t.Contains("elpriser i morgen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tomorrow_ThatIsMuchDearerThanRecentDays_SendsOneMessage()
    {
        using var app = await StartAsync();
        app.Fake.SpotDayFactors[Tomorrow] = 1.5;
        await TickAtAsync(app, 8, 6);
        Assert.Empty(app.Telegrams); // tomorrow is not published yet

        await TickAtAsync(app, 13, 31);

        var message = Assert.Single(app.Telegrams, t => t.Contains("elpriser i morgen", StringComparison.Ordinal));
        var lines = message.Split('\n');
        Assert.Equal("Energy Monitor · elpriser i morgen, 9. november", lines[0]);
        Assert.StartsWith("Hjem, Sommerhus (DK1): gns. 1,2", lines[1]);
        Assert.Contains("Billigst kl. 06–09 (", lines[1]);
        Assert.Contains("dyrest kl. 18–21 (", lines[1]);
        Assert.StartsWith("Gennemsnittet er 4", lines[2]);
        Assert.Contains(" % højere end de seneste dage (0,8", lines[2]);
        Assert.Equal(3, lines.Length);

        await TickAtAsync(app, 17, 1); // the evening sync fetches the same prices again
        Assert.Single(app.Telegrams, t => t.Contains("elpriser i morgen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tomorrow_WithNegativeHours_SendsAMessage_AlsoFromAManualSync()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 14, 0, 0, TimeSpan.Zero)); // 15:00
        using var app = new AppFactory(pricing: true, time: time);
        await app.SeedPropertiesAsync();
        await app.Get<PricingStore>().SaveConfigurationAsync("hjem", new([Spot], [], [], false), default);
        app.Fake.SpotNegativeDays.Add(Tomorrow);

        await app.SyncAsync();
        await app.SyncAsync();

        var message = Assert.Single(app.Telegrams);
        Assert.Contains("Hjem (DK1): gns. ", message);
        Assert.EndsWith("2 timer med negativ spotpris.", message);
    }

    [Fact]
    public async Task Tomorrow_SendsNothing_WhenItsPricesAreNotPublished_OrTheMessageIsTurnedOff()
    {
        using (var withheld = await StartAsync())
        {
            withheld.Fake.SpotDayFactors[Tomorrow] = 1.5;
            withheld.Fake.SpotTomorrowWithheld = true;
            await TickAtAsync(withheld, 13, 31);
            await TickAtAsync(withheld, 17, 1);
            Assert.DoesNotContain(withheld.Telegrams, t => t.Contains("elpriser i morgen", StringComparison.Ordinal));
        }

        using var off = await StartAsync();
        off.Fake.SpotDayFactors[Tomorrow] = 1.5;
        var settings = off.Get<NotificationSettingsService>();
        await settings.SetAsync(await settings.GetAsync(default) with { TomorrowPricesEnabled = false }, default);
        await TickAtAsync(off, 13, 31);
        Assert.DoesNotContain(off.Telegrams, t => t.Contains("elpriser i morgen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AfternoonSlot_DoesNotExist_WhenPricingIsOff()
    {
        using var app = await StartAsync(pricing: false);

        await TickAtAsync(app, 13, 31);

        Assert.Equal(0, app.Fake.SpotCalls);
    }

    [Fact]
    public async Task NotificationSettings_KeepTheTomorrowMessageOn_UnlessItIsTurnedOff()
    {
        using var app = await StartAsync();
        using var client = app.ClientFor(AppFactory.Admin);
        Assert.True((await client.GetJsonAsync("/api/admin/settings/notifications")).GetProperty("tomorrowPricesEnabled").GetBoolean());

        // A client from before the setting existed does not send it.
        await client.PutAsJsonAsync("/api/admin/settings/notifications", new { dailyReportEnabled = true, recipients = new[] { "1000" } });
        Assert.True((await client.GetJsonAsync("/api/admin/settings/notifications")).GetProperty("tomorrowPricesEnabled").GetBoolean());

        await client.PutAsJsonAsync("/api/admin/settings/notifications", new { dailyReportEnabled = true, recipients = new[] { "1000" }, tomorrowPricesEnabled = false });
        Assert.False((await client.GetJsonAsync("/api/admin/settings/notifications")).GetProperty("tomorrowPricesEnabled").GetBoolean());
        Assert.False((await app.Get<NotificationSettingsService>().GetAsync(default)).TomorrowPricesEnabled);
    }

    [Fact]
    public async Task Mcp_GivesTheSameHourlyPrices_AsTheApi()
    {
        using var app = await StartAsync(mcp: true, local: true);
        await app.SyncAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var call = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "get_spot_prices", arguments = new { propertyId = "hjem", from = "2026-11-08", to = "2026-11-08" } } });
        var text = await call.Content.ReadAsStringAsync();
        if (text.StartsWith("event:", StringComparison.Ordinal))
            text = text.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        using var json = JsonDocument.Parse(text);
        var structured = json.RootElement.GetProperty("result").GetProperty("structuredContent");

        var api = await client.GetJsonAsync("/api/energy/hjem/prices?from=2026-11-08&to=2026-11-08");
        Assert.Equal("DK1", structured.GetProperty("area").GetString());
        var (fromMcp, fromApi) = (structured.GetProperty("days")[0], api.GetProperty("days")[0]);
        Assert.Equal(fromApi.GetProperty("average").GetDecimal(), fromMcp.GetProperty("average").GetDecimal());
        Assert.Equal(fromApi.GetProperty("hours")[18].GetProperty("total").GetDecimal(), fromMcp.GetProperty("hours")[18].GetProperty("total").GetDecimal());
        Assert.Equal(fromApi.GetProperty("cheapest").GetProperty("fromUtc").GetDateTimeOffset(), fromMcp.GetProperty("cheapest").GetProperty("fromUtc").GetDateTimeOffset());
    }
}
