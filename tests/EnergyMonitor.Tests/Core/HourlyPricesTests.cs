using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Core.Reports;
using EnergyMonitor.Core.Time;

namespace EnergyMonitor.Tests.Core;

/// <summary>The price of a kWh hour by hour, and the Telegram texts made from it.</summary>
public sealed class HourlyPricesTests
{
    private static readonly TimeZoneInfo Zone = LocalDay.Zone(Property.DefaultTimeZone);
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly DateTimeOffset DayStart = LocalDay.StartUtc(Day, Zone);
    private static readonly ElectricityAgreement Spot = new(DayStart.AddDays(-30), null, "DK1", "Spot", null, 0.1m, 2m, 60, 0.25m);

    private static TariffRate Rate(string code, string component, decimal price, int fromHour = 0, int toHour = 24, string vat = "Excluded", string unit = "kWh") =>
        new(code, "owner", component, DayStart.AddDays(-1), DayStart.AddDays(2), price, unit, vat, "test", fromHour, toHour);

    /// <summary>Quarter-hour prices for a whole local day; the price is chosen from the local hour.</summary>
    private static SpotPrice[] Quarters(DateOnly day, Func<int, decimal> price, string area = "DK1")
    {
        var (start, end) = LocalDay.Bounds(day, Zone);
        var prices = new List<SpotPrice>();
        for (var t = start; t < end; t = t.AddMinutes(15))
        {
            prices.Add(new(area, t, t.AddMinutes(15), price(TimeZoneInfo.ConvertTime(t, Zone).Hour)));
        }

        return [.. prices];
    }

    [Fact]
    public void Total_IsSpot_Markup_TariffsOfTheHour_AndVat_WithoutSubscriptions()
    {
        TariffRate[] rates =
        [
            Rate("net", "Network", 0.2m), Rate("peak", "Network", 1m, 17, 21), Rate("tax", "Tax", 0.4m),
            Rate("subscription", "Subscription", 30m, unit: "day"),
        ];

        var day = HourlyPrices.ForDay(Day, Zone, Quarters(Day, _ => 1m), Spot, rates, 3);

        Assert.Equal(DayPrices.Complete, day.Status);
        Assert.Equal(24, day.Hours.Count);
        var night = day.Hours[0];
        Assert.Equal((1m, 1m, 0.1m, 0.2m, 0.4m, 0.425m, 2.125m), (night.Spot, night.Energy, night.Supplier, night.Network, night.Tax, night.Vat, night.Total));
        Assert.Equal(night.Total, night.Energy + night.Supplier + night.Network + night.Tax + night.Vat);
        Assert.Equal(3.375m, day.Hours[18].Total);
        Assert.Equal((2.125m, 3.375m), (day.Min, day.Max));
        Assert.Equal(((20 * 2.125m) + (4 * 3.375m)) / 24, day.Average);
    }

    [Fact]
    public void Periods_AreConsecutiveHours_AndTheEarliestWinsATie()
    {
        var day = HourlyPrices.ForDay(Day, Zone, Quarters(Day, h => h is >= 17 and < 21 ? 3m : h is 3 or 4 ? 0.5m : 1m), Spot, [], 3);

        // 02–05 and 03–06 are equally cheap: the earliest is chosen.
        Assert.Equal((DayStart.AddHours(2), DayStart.AddHours(5)), (day.Cheapest!.FromUtc, day.Cheapest.ToUtc));
        Assert.Equal((DayStart.AddHours(17), DayStart.AddHours(20), 3.875m), (day.MostExpensive!.FromUtc, day.MostExpensive.ToUtc, day.MostExpensive.Average));
    }

    [Fact]
    public void RestOfTheDay_LooksOnlyAtHoursNotOverYet_AndGivesNoPeriodWhenTooFewAreLeft()
    {
        var prices = Quarters(Day, h => h < 6 ? 0.2m : h < 12 ? 1m : 2m);

        var morning = HourlyPrices.ForDay(Day, Zone, prices, Spot, [], 3, DayStart.AddHours(9).AddMinutes(1));
        Assert.Equal(DayStart.AddHours(9), morning.Cheapest!.FromUtc); // the hour in progress still counts
        Assert.Equal(DayStart.AddHours(12), morning.MostExpensive!.FromUtc);
        Assert.Equal(0.375m, morning.Min); // the whole day, also the hours that are over

        var late = HourlyPrices.ForDay(Day, Zone, prices, Spot, [], 3, DayStart.AddHours(22).AddMinutes(1));
        Assert.Null(late.Cheapest);
        Assert.Null(late.MostExpensive);
        Assert.NotNull(late.Average);
    }

    [Fact]
    public void QuarterPrices_AreAveragedPerHour_AndAMissingQuarterLeavesTheHourUnpriced()
    {
        var prices = Quarters(Day, _ => 1m).ToList();
        for (var q = 0; q < 4; q++)
        {
            prices[q] = prices[q] with { DkkPerKWh = q };
        }

        Assert.Equal(1.5m, HourlyPrices.ForDay(Day, Zone, prices, Spot, [], 3).Hours[0].Spot);

        prices.RemoveAt(4 * 5); // the first quarter of 05–06
        var partial = HourlyPrices.ForDay(Day, Zone, prices, Spot, [], 3);
        Assert.Equal(DayPrices.Partial, partial.Status);
        Assert.Null(partial.Hours[5].Spot);
        Assert.Null(partial.Hours[5].Total);
        Assert.NotNull(partial.Hours[6].Total);
        Assert.Null(partial.Average);
        Assert.Null(partial.Cheapest);
    }

    [Fact]
    public void NoPrices_IsPending_AndOtherAreasAreIgnored()
    {
        var day = HourlyPrices.ForDay(Day, Zone, Quarters(Day, _ => 1m, "DK2"), Spot, [Rate("net", "Network", 0.2m)], 3);

        Assert.Equal(DayPrices.Pending, day.Status);
        Assert.Equal(24, day.Hours.Count);
        Assert.All(day.Hours, h => Assert.Null(h.Total));
    }

    [Theory]
    [InlineData("2026-03-29", 23)]
    [InlineData("2026-10-25", 25)]
    public void DaylightSavingDays_Have23And25Hours(string date, int hours)
    {
        var day = DateOnly.Parse(date);

        var result = HourlyPrices.ForDay(day, Zone, Quarters(day, _ => 1m), Spot, [], 3);

        Assert.Equal(hours, result.Hours.Count);
        Assert.Equal(DayPrices.Complete, result.Status);
        Assert.Equal(LocalDay.Bounds(day, Zone), (result.Hours[0].StartUtc, result.Hours[^1].EndUtc));
    }

    [Fact]
    public void NegativePrices_AreKept_AndCounted()
    {
        var day = HourlyPrices.ForDay(Day, Zone, Quarters(Day, h => h is 2 or 3 ? -0.5m : 1m), Spot, [], 3);

        Assert.Equal(2, day.NegativeHours);
        Assert.Equal(-0.5m, day.Hours[2].Total); // (−0,5 + 0,1) × 1,25
        Assert.Equal(-0.5m, day.Min);
    }

    [Fact]
    public void FixedPrice_NeedsNoSpotPrice_AndOnlyTheTariffsVary()
    {
        var terms = Spot with { Kind = "Fixed", FixedPrice = 0.8m, SupplierMarkup = null };

        var day = HourlyPrices.ForDay(Day, Zone, [], terms, [Rate("peak", "Network", 1m, 17, 21)], 3);

        Assert.Equal(DayPrices.Complete, day.Status);
        Assert.Null(day.Hours[0].Spot);
        Assert.Equal(1m, day.Hours[0].Total);
        Assert.Equal(2.25m, day.Hours[17].Total);
    }

    [Fact]
    public void TariffVat_FollowsTheRate_AndWithoutAnAgreementSpotAnd25PercentVatAreAssumed()
    {
        TariffRate[] rates = [Rate("included", "Network", 0.5m, vat: "Included"), Rate("exempt", "Tax", 0.3m, vat: "Exempt")];

        var hour = HourlyPrices.ForDay(Day, Zone, Quarters(Day, _ => 1m), null, rates, 3).Hours[0];

        Assert.Equal((0.4m, 0.3m, 0.35m, 2.05m), (hour.Network, hour.Tax, hour.Vat, hour.Total));
    }

    [Fact]
    public void WindowLength_IsKeptBetween1And6Hours()
    {
        var prices = Quarters(Day, _ => 1m);

        Assert.Equal(TimeSpan.FromHours(6), Length(HourlyPrices.ForDay(Day, Zone, prices, Spot, [], 40).Cheapest!));
        Assert.Equal(TimeSpan.FromHours(1), Length(HourlyPrices.ForDay(Day, Zone, prices, Spot, [], 0).Cheapest!));

        static TimeSpan Length(PriceWindow w) => w.ToUtc - w.FromUtc;
    }

    private static PriceGroup Group(Func<int, decimal> price, DateTimeOffset? notBefore = null, params string[] names) =>
        new(names.Length > 0 ? names : ["Sommerhus"], "DK1", HourlyPrices.ForDay(Day, Zone, Quarters(Day, price), Spot, [], 3, notBefore), Zone);

    [Fact]
    public void Text_FormatsAmountsAndHoursInDanish()
    {
        Assert.Equal("2,31", PriceText.Kr(2.305m));
        Assert.Equal("−0,05", PriceText.Kr(-0.05m));
        Assert.Equal("kl. 02–05", PriceText.Hours(DayStart.AddHours(2), DayStart.AddHours(5), Zone));
        Assert.Equal("kl. 21–24", PriceText.Hours(DayStart.AddHours(21), DayStart.AddHours(24), Zone));
    }

    [Fact]
    public void TodayLine_NamesThePropertiesTheAverageAndTheRestOfTheDay()
    {
        var group = Group(h => h is >= 13 and < 16 ? 0.5m : h is >= 17 and < 20 ? 3m : 1m, DayStart.AddHours(9), "Hjem", "Sommerhus");

        Assert.Equal(
            "Elpris i dag (Hjem, Sommerhus, DK1): gns. 1,61 kr./kWh. Resten af dagen: billigst kl. 13–16 (0,75), dyrest kl. 17–20 (3,88).",
            PriceText.TodayLine(group));
    }

    [Fact]
    public void TodayLine_IsLeftOutWithoutCompletePrices_AndHasNoPeriodsWhenThePriceIsFlat()
    {
        var pending = new PriceGroup(["Hjem"], "DK1", HourlyPrices.ForDay(Day, Zone, [], Spot, [], 3), Zone);
        Assert.Null(PriceText.TodayLine(pending));

        Assert.Equal("Elpris i dag (Sommerhus, DK1): gns. 1,38 kr./kWh.", PriceText.TodayLine(Group(_ => 1m)));
    }

    [Fact]
    public void Remarkable_IsEmptyForAnOrdinaryDay()
    {
        var group = Group(h => h is >= 17 and < 20 ? 1.4m : 1m);

        Assert.Empty(PriceText.Remarkable(group, group.Day.Average * 1.1m, 25, 4m));
        Assert.Empty(PriceText.Remarkable(group, null, 25, 4m));
    }

    [Fact]
    public void Remarkable_NamesNegativeHours_ADeviationFromRecentDays_AndAHighHour()
    {
        var negative = Group(h => h == 3 ? -0.2m : 1m);
        Assert.Equal(["1 time med negativ spotpris."], PriceText.Remarkable(negative, negative.Day.Average, 25, 4m));

        var flat = Group(_ => 1m); // 1,375 kr./kWh
        Assert.Equal(["Gennemsnittet er 31 % lavere end de seneste dage (2,00 kr./kWh)."], PriceText.Remarkable(flat, 2m, 25, 4m));
        Assert.Equal(["Gennemsnittet er 38 % højere end de seneste dage (1,00 kr./kWh)."], PriceText.Remarkable(flat, 1m, 25, 4m));
        Assert.Empty(PriceText.Remarkable(flat, 1m, 0, 4m)); // the comparison is turned off

        var peak = Group(h => h == 18 ? 3.5m : 1m);
        Assert.Equal(["Dyreste time: kl. 18–19, 4,50 kr./kWh."], PriceText.Remarkable(peak, peak.Day.Average, 25, 4m));
        Assert.Empty(PriceText.Remarkable(peak, peak.Day.Average, 25, 0)); // the high price is turned off
    }

    [Fact]
    public void TomorrowMessage_HasALinePerGroup_FollowedByItsReasons()
    {
        var group = Group(h => h is 2 or 3 ? -0.5m : h is >= 17 and < 20 ? 3m : 1m);

        var text = PriceText.TomorrowMessage(Day, [(group, PriceText.Remarkable(group, null, 25, 4m))]);

        Assert.Equal(
            "Energy Monitor · elpriser i morgen, 10. november\n"
            + "Sommerhus (DK1): gns. 1,53 kr./kWh. Billigst kl. 01–04 (0,13), dyrest kl. 17–20 (3,88).\n"
            + "2 timer med negativ spotpris.",
            text);
    }
}
