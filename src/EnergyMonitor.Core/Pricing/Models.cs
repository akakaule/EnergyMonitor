namespace EnergyMonitor.Core.Pricing;

public sealed record SpotPrice(string Area, DateTimeOffset StartUtc, DateTimeOffset EndUtc, decimal DkkPerKWh,
    string Currency = "DKK", string Unit = "kWh", string Source = "Energinet.DayAheadPrices");

public sealed record ElectricityAgreement(DateTimeOffset FromUtc, DateTimeOffset? ToUtc, string Area,
    string Kind, decimal? FixedPrice, decimal? SupplierMarkup, decimal? DailySubscription,
    int SettlementMinutes, decimal VatRate);

/// <summary>Validity is half open. Amounts are DKK per Unit. Code identifies a charge across sources.</summary>
public sealed record TariffRate(string Code, string Owner, string Component, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, decimal? Price, string Unit, string Vat, string Source,
    int FromHour = 0, int ToHour = 24);

public sealed record PricingConfiguration(IReadOnlyList<ElectricityAgreement> Agreements,
    IReadOnlyList<TariffRate> Rates, IReadOnlyList<string> RequiredTariffCodes, bool TariffsConfirmed)
{
    public static readonly PricingConfiguration Empty = new([], [], [], false);
    public IReadOnlyList<TariffMapping> TariffMappings { get; init; } = [];
}

/// <summary>Operator-verified interpretation of an upstream charge; never infer VAT or units from a code.</summary>
public sealed record TariffMapping(string Code, string Owner, string Type, string Component, string Unit,
    string Vat, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

public sealed record TariffLink(string Code, string Owner, string Type, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, int Factor);

public sealed record PriceSourceStatus(string Source, DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? ErrorCode);

public sealed record CostResult(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string Currency, string Unit,
    string TimeZone, string Status, bool IsEstimated, decimal ConsumptionCoverage, decimal PriceCoverage,
    decimal KnownKWh, decimal KnownSubtotal, decimal? Total, IReadOnlyDictionary<string, decimal> Components,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Sources, string Method);
