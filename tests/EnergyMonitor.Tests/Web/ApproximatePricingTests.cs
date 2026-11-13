using System.Net.Http.Json;
using System.Text.Json;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Web.Pricing;

namespace EnergyMonitor.Tests.Web;

/// <summary>"Ca. pris": consumption priced with spot prices and the charges Eloverblik reports today.</summary>
public class ApproximatePricingTests
{
    /// <summary>The shape of Eloverblik's getcharges answer, with Hus' charges.</summary>
    public const string CurrentCharges = """
        {"result":[{"result":{
          "fees":[],
          "subscriptions":[
            {"name":"Net abo C forbrug","owner":"5790000705689","price":40.835055,"periodType":"P1M"},
            {"name":"Net abo stikledning","owner":"5790000705689","price":4,"periodType":"P1M"},
            {"name":"TSO - System Abonnement","owner":"5790000432752","price":15.583333,"periodType":"P1M"}],
          "tariffs":[
            {"name":"Systemtarif","owner":"5790000432752","periodType":"P1D","prices":[{"position":"1","price":0.072}]},
            {"name":"Transmissions nettarif","owner":"5790000432752","periodType":"P1D","prices":[{"position":"1","price":0.043}]},
            {"name":"Elafgift","owner":"5790000432752","periodType":"P1D","prices":[{"position":"1","price":0.008}]},
            {"name":"Nettarif C","owner":"5790000705689","periodType":"PT1H","prices":[
              {"position":"1","price":0.106175},{"position":"2","price":0.106175},{"position":"3","price":0.106175},
              {"position":"4","price":0.106175},{"position":"5","price":0.106175},{"position":"6","price":0.106175},
              {"position":"7","price":0.318524},{"position":"8","price":0.318524},{"position":"9","price":0.318524},
              {"position":"10","price":0.318524},{"position":"11","price":0.318524},{"position":"12","price":0.318524},
              {"position":"13","price":0.318524},{"position":"14","price":0.318524},{"position":"15","price":0.318524},
              {"position":"16","price":0.318524},{"position":"17","price":0.318524},{"position":"18","price":0.955573},
              {"position":"19","price":0.955573},{"position":"20","price":0.955573},{"position":"21","price":0.955573},
              {"position":"22","price":0.318524},{"position":"23","price":0.318524},{"position":"24","price":0.318524}]}]},
          "success":true}]}
        """;

    private static readonly DateTimeOffset DayStart = DateTimeOffset.Parse("2026-11-09T23:00:00Z"); // 10/11 00:00 in Denmark
    private static readonly DateTimeOffset DayEnd = DayStart.AddDays(1);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void CurrentCharges_BecomeDailySubscriptions_FlatTariffs_AndHourBands()
    {
        var rates = ApproximatePricing.FromCurrentCharges(Json(CurrentCharges), DayStart, DayEnd);

        var subscriptions = rates.Where(r => r.Unit == "day").ToArray();
        Assert.Equal(3, subscriptions.Length);
        Assert.All(subscriptions, r => Assert.Equal("Subscription", r.Component));
        Assert.Equal(40.835055m * 12 / 365, subscriptions.Single(r => r.Code == "Net abo C forbrug").Price);

        Assert.Equal("Tax", rates.Single(r => r.Code == "Elafgift").Component);
        Assert.Equal("Network", rates.Single(r => r.Code == "Systemtarif").Component);

        var bands = rates.Where(r => r.Code == "Nettarif C").OrderBy(r => r.FromHour).ToArray();
        Assert.Equal(24, bands.Length);
        Assert.Equal((17, 18, 0.955573m), (bands[17].FromHour, bands[17].ToHour, bands[17].Price));
        Assert.Equal((0, 1, 0.106175m), (bands[0].FromHour, bands[0].ToHour, bands[0].Price));
        Assert.All(rates, r => Assert.Equal("Excluded", r.Vat));
        Assert.All(rates, r => Assert.Equal((DayStart, DayEnd), (r.FromUtc, r.ToUtc)));
    }

    [Fact]
    public void Estimate_PricesEachHour_WithSpot_Supplier_Tariffs_SubscriptionsAndVat()
    {
        // 1 kWh between 17:00 and 18:00 Danish time, the evening peak band.
        var hour = DayStart.AddHours(17);
        var intervals = new[] { new ConsumptionInterval("hus", "eloverblik", hour, hour.AddHours(1), 1, "PT1H", IntervalQuality.Measured, hour) };
        var prices = new[] { new SpotPrice("DK1", hour, hour.AddHours(1), 2.0m) };
        var terms = new ElectricityAgreement(DayStart.AddDays(-30), null, "DK1", "Spot", null, 0.1m, 0.5m, 60, 0.25m);

        var estimate = ApproximatePricing.Estimate(DayStart, DayEnd, intervals, prices, Json(CurrentCharges), terms);

        var subscriptions = (40.835055m + 4 + 15.583333m) * 12 / 365 + 0.5m;
        var expected = (2.0m + 0.1m + 0.955573m + 0.072m + 0.043m + 0.008m + subscriptions) * 1.25m;
        Assert.Equal(expected, estimate.Total, 6);
        Assert.Equal(1m, estimate.KWh);
        Assert.Equal(2.0m, estimate.Components["Energy"], 6);
        Assert.Equal(["current_tariffs"], estimate.Assumptions);
    }

    [Fact]
    public void Estimate_WithoutRegisteredTerms_CountsTheSupplierAsZero_AndSaysSo()
    {
        var estimate = ApproximatePricing.Estimate(DayStart, DayEnd, [], [], Json(CurrentCharges), null);

        Assert.Contains("supplier_markup_missing", estimate.Assumptions);
        Assert.Contains("supplier_subscription_missing", estimate.Assumptions);
        Assert.Equal(0m, estimate.Components["Supplier"]);
    }

    [Fact]
    public void Estimate_WithoutCurrentCharges_ReportsThemMissing()
    {
        var historical = Json("""{"result":{"results":[{"chargeLinks":[]}],"chargeInformations":[]}}""");

        var estimate = ApproximatePricing.Estimate(DayStart, DayEnd, [], [], historical, null);

        Assert.Contains("charges_missing", estimate.Assumptions);
        Assert.Equal(0m, estimate.Components["Network"]);
    }

    [Fact]
    public async Task EstimateEndpoint_ReturnsATotal_ForAViewer()
    {
        using var app = new AppFactory(pricing: true);
        await app.SeedPropertiesAsync();
        await app.SyncAsync();
        var store = app.Get<PricingStore>();
        var start = DateTimeOffset.Parse("2026-10-31T23:00:00Z");
        await store.SavePricesAsync([.. Enumerable.Range(0, 24).Select(h => new SpotPrice("DK1", start.AddHours(h), start.AddHours(h + 1), 1m))], default);
        await store.SaveChargeEvidenceAsync("hjem", "2026-11-01", CurrentCharges, default);
        using var viewer = app.ClientFor(AppFactory.Viewer);

        var body = await viewer.GetFromJsonAsync<JsonElement>("/api/energy/hjem/estimate?from=2026-11-01&to=2026-11-01");

        Assert.True(body.GetProperty("total").GetDecimal() > 0);
        Assert.True(body.GetProperty("kWh").GetDecimal() > 0);
        Assert.Contains("current_tariffs", body.GetProperty("assumptions").EnumerateArray().Select(a => a.GetString()));
    }
}
