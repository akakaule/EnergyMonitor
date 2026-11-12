using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Web.Pricing;

namespace EnergyMonitor.Tests.Web;

public sealed class ChargeNormalizerTests
{
    [Fact]
    public void VerifiedMappings_IntersectLinkAndRateValidity_AndApplyFactor()
    {
        const string json = """
        {"result":{"results":[{"chargeLinks":[{"chargeIdentifier":{"code":"grid","owner":"operator","type":"D03"},"chargeLinkPeriods":[{"from":"2026-11-01T00:00:00Z","to":"2026-11-02T00:00:00Z","factor":2}]}]}],"chargeInformations":[{"chargeIdentifier":{"code":"grid","owner":"operator","type":"D03"},"chargeSeriesPoints":[{"from":"2026-10-01T00:00:00Z","to":"2026-12-01T00:00:00Z","price":0.123456789}]}]}}
        """;
        var from = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var to = from.AddDays(2);
        Assert.True(ChargeNormalizer.Normalize(json, [], from, to).HasUnmappedCharges);
        var normalized = ChargeNormalizer.Normalize(json, [new TariffMapping("grid", "operator", "D03", "Network", "kWh", "Excluded", from, to)], from, to);
        Assert.False(normalized.HasUnmappedCharges);
        Assert.Single(normalized.Links);
        var rate = Assert.Single(normalized.Rates);
        Assert.Equal(0.246913578m, rate.Price);
        Assert.Equal(from, rate.FromUtc);
        Assert.Equal(from.AddDays(1), rate.ToUtc);
    }
}
