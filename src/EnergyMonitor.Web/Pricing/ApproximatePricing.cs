using System.Globalization;
using System.Text.Json;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Pricing;

namespace EnergyMonitor.Web.Pricing;

/// <summary>
/// An approximate price ("ca. pris") for a period. Unlike <see cref="CostResult"/>, it always gives a total:
/// what is not known is assumed and listed in <see cref="Assumptions"/>.
/// </summary>
public sealed record ApproximateEstimate(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    decimal Total,
    decimal KWh,
    decimal ConsumptionCoverage,
    IReadOnlyDictionary<string, decimal> Components,
    IReadOnlyList<string> Assumptions);

/// <summary>
/// Prices consumption with spot prices, the supplier's registered terms and the charges Eloverblik reports
/// for the metering point today. Today's tariffs are applied to the whole period, so seasonal and past
/// tariff changes are not reflected; the strict <see cref="CostCalculator"/> result stays the reference.
/// </summary>
public static class ApproximatePricing
{
    public const string CurrentTariffs = "current_tariffs";
    public const string ChargesMissing = "charges_missing";
    public const string SupplierMarkupMissing = "supplier_markup_missing";
    public const string SupplierSubscriptionMissing = "supplier_subscription_missing";
    private const decimal DanishVat = 0.25m;

    /// <summary>
    /// Reads Eloverblik's current charges (getcharges): monthly subscriptions become a price per day, tariffs per
    /// day or per hour become prices per kWh, valid for the whole range. Eloverblik's prices are excl. VAT.
    /// Returns nothing for another shape, such as the historical charge-link format.
    /// </summary>
    public static IReadOnlyList<TariffRate> FromCurrentCharges(JsonElement evidence, DateTimeOffset from, DateTimeOffset to)
    {
        var rates = new List<TariffRate>();
        if (evidence.ValueKind != JsonValueKind.Object || !evidence.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return rates;
        }

        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("result", out var charges) || charges.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var subscription in Items(charges, "subscriptions"))
            {
                if (Text(subscription, "periodType") == "P1M" && Number(subscription, "price") is { } monthly)
                {
                    rates.Add(Rate(subscription, "Subscription", from, to, monthly * 12 / 365, "day", 0, 24));
                }
            }

            foreach (var tariff in Items(charges, "tariffs"))
            {
                var name = Text(tariff, "name") ?? "";
                var component = name.Contains("afgift", StringComparison.OrdinalIgnoreCase) ? "Tax" : "Network";
                var prices = Items(tariff, "prices").ToArray();
                switch (Text(tariff, "periodType"))
                {
                    case "P1D" when prices.Length == 1 && Number(prices[0], "price") is { } flat:
                        rates.Add(Rate(tariff, component, from, to, flat, "kWh", 0, 24));
                        break;
                    case "PT1H":
                        foreach (var price in prices)
                        {
                            if (int.TryParse(Text(price, "position"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var position)
                                && position is >= 1 and <= 24 && Number(price, "price") is { } hourly)
                            {
                                rates.Add(Rate(tariff, component, from, to, hourly, "kWh", position - 1, position));
                            }
                        }

                        break;
                }
            }
        }

        return rates;
    }

    public static ApproximateEstimate Estimate(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<ConsumptionInterval> consumption,
        IReadOnlyList<SpotPrice> prices, JsonElement? evidence, ElectricityAgreement? terms)
    {
        var assumptions = new List<string>();
        var rates = evidence is { } json ? FromCurrentCharges(json, from, to) : [];
        assumptions.Add(rates.Count > 0 ? CurrentTariffs : ChargesMissing);
        if (terms?.SupplierMarkup is null && terms?.Kind != "Fixed")
        {
            assumptions.Add(SupplierMarkupMissing);
        }

        if (terms?.DailySubscription is null)
        {
            assumptions.Add(SupplierSubscriptionMissing);
        }

        // The latest registered terms stand in for the whole period; unknown supplier amounts count as zero.
        var agreement = new ElectricityAgreement(from, null, terms?.Area ?? "DK1", terms?.Kind ?? "Spot", terms?.FixedPrice,
            terms?.SupplierMarkup ?? 0, terms?.DailySubscription ?? 0, 60, terms?.VatRate ?? DanishVat);
        var result = CostCalculator.Calculate(from, to, consumption, [agreement], prices, rates, [], tariffsConfirmed: true);
        return new(from, to, result.KnownSubtotal, result.KnownKWh, result.ConsumptionCoverage, result.Components, assumptions);
    }

    private static TariffRate Rate(JsonElement charge, string component, DateTimeOffset from, DateTimeOffset to, decimal price, string unit, int fromHour, int toHour) =>
        new(Text(charge, "name") ?? "charge", Text(charge, "owner") ?? "", component, from, to, price, unit, "Excluded", "Eloverblik.current", fromHour, toHour);

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        } : null;

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;
}
