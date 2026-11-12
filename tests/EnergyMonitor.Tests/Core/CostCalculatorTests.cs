using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;

namespace EnergyMonitor.Tests.Core;

public sealed class CostCalculatorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
    private static ElectricityAgreement Agreement => new(Start, Start.AddDays(2), "DK1", "Spot", null, 0.1m, 3m, 60, 0.25m);
    private static TariffRate Rate => new("grid", "owner", "Network", Start, Start.AddDays(2), 0.2m, "kWh", "Excluded", "manual");
    private static ConsumptionInterval Interval(DateTimeOffset start, int minutes = 60) => new("home", "eloverblik", start, start.AddMinutes(minutes), 2, "PT1H", IntervalQuality.Measured, Start);
    private static CostResult Calculate(IReadOnlyList<ConsumptionInterval> consumption, IReadOnlyList<SpotPrice> prices, ElectricityAgreement? agreement = null, IReadOnlyList<TariffRate>? rates = null, int minutes = 60) =>
        CostCalculator.Calculate(Start, Start.AddMinutes(minutes), consumption, [agreement ?? Agreement], prices, rates ?? [Rate], ["grid"], true);

    [Fact]
    public void NegativePrices_VatAndSubscriptions_AreNotRoundedPerInterval()
    {
        var result = Calculate([Interval(Start)], [new("DK1", Start, Start.AddHours(1), -0.5m)]);
        Assert.Equal(-0.4m, result.Components["Energy"] + result.Components["Supplier"] + result.Components["Network"]);
        Assert.Equal(0.125m, result.Components["Subscription"]);
        Assert.Equal(-0.34375m, result.Total);
        Assert.Equal("Calculated", result.Status);
    }

    [Fact]
    public void MissingPrice_PreservesKnownSubtotal_ButHasNoTotal()
    {
        var result = Calculate([Interval(Start)], []);
        Assert.Null(result.Total);
        Assert.Contains("spot", result.Missing);
        Assert.Equal("Partial", result.Status);
        Assert.True(result.KnownSubtotal > 0);
    }

    [Fact]
    public void HourlyConsumption_QuarterSettlement_IsExplicitlyEstimated()
    {
        var prices = Enumerable.Range(0, 4).Select(i => new SpotPrice("DK1", Start.AddMinutes(i * 15), Start.AddMinutes((i + 1) * 15), i)).ToArray();
        var result = Calculate([Interval(Start)], prices, Agreement with { SettlementMinutes = 15 });
        Assert.True(result.IsEstimated);
        Assert.Equal(3m, result.Components["Energy"]);
    }

    [Fact]
    public void IncompleteFineResolution_DoesNotDoubleCountCoarseIntervals()
    {
        var result = Calculate([Interval(Start), Interval(Start, 15)], [new("DK1", Start, Start.AddHours(1), 1)]);
        Assert.Null(result.Total);
        Assert.Equal(0.25m, result.ConsumptionCoverage);
        Assert.Equal(2m, result.KnownKWh);
    }

    [Theory]
    [InlineData("2026-03-29", 23)]
    [InlineData("2026-10-25", 25)]
    public void DanishDays_SubscriptionIsOneCalendarDay(string day, int hours)
    {
        var date = DateOnly.Parse(day);
        var (start, end) = LocalDay.Bounds(date, LocalDay.Zone(Property.DefaultTimeZone));
        Assert.Equal(hours, (end - start).TotalHours);
        var a = Agreement with { FromUtc = start, ToUtc = end, Kind = "Fixed", FixedPrice = 1 };
        var result = CostCalculator.Calculate(start, end, [Interval(start) with { EndUtc = end, Resolution = "P1D" }], [a], [], [Rate with { FromUtc = start, ToUtc = end }], ["grid"], true);
        Assert.Equal(3m, result.Components["Subscription"]);
    }

    [Fact]
    public void UnknownSupplierTerms_AreNeverZero()
    {
        var result = Calculate([Interval(Start)], [new("DK1", Start, Start.AddHours(1), 1)], Agreement with { SupplierMarkup = null });
        Assert.Null(result.Total);
        Assert.Contains("supplier", result.Missing);
    }

    [Fact]
    public void QuarterMeasurements_UseContractHourlyAverage()
    {
        var readings = Enumerable.Range(0, 4).Select(n => Interval(Start.AddMinutes(n * 15), 15) with { KWh = n == 0 ? 4 : 0 }).ToArray();
        var prices = Enumerable.Range(0, 4).Select(n => new SpotPrice("DK1", Start.AddMinutes(n * 15), Start.AddMinutes((n + 1) * 15), n)).ToArray();
        var result = Calculate(readings, prices);
        Assert.Equal(6m, result.Components["Energy"]);
        Assert.False(result.IsEstimated);
    }

    [Fact]
    public void TariffSwitch_IncludedVat_AndAgreementHistory_AreAppliedAtTheirBoundaries()
    {
        var rates = new[] { Rate with { ToUtc = Start.AddMinutes(30), Price = 1.25m, Vat = "Included" },
            Rate with { FromUtc = Start.AddMinutes(30), Price = 2m, Vat = "Exempt" } };
        var result = Calculate([Interval(Start)], [new("DK1", Start, Start.AddHours(1), 0)], rates: rates);
        Assert.Equal(3m, result.Components["Network"]);
        Assert.True(result.IsEstimated);
        Assert.NotNull(result.Total);
    }

    [Fact]
    public void MissingConsumption_CanStillHaveKnownSubscription_ButNeverTotal()
    {
        var result = Calculate([], []);
        Assert.Null(result.Total);
        Assert.Equal("Unavailable", result.Status);
        Assert.Equal(0.125m, result.Components["Subscription"]);
    }

    [Fact]
    public void DailyConsumption_VariableTariff_IsEstimated_EvenWithFixedEnergy()
    {
        var a = Agreement with { Kind = "Fixed", FixedPrice = 1 };
        var rates = new[] { Rate with { ToHour = 12, Price = 1 }, Rate with { FromHour = 12, Price = 2 } };
        var result = Calculate([Interval(Start) with { EndUtc = Start.AddDays(1), Resolution = "P1D" }], [], a, rates, 1440);
        Assert.True(result.IsEstimated);
    }
}
