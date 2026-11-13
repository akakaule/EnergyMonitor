using System.Globalization;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Core.Time;
using EnergyMonitor.Web.Eloverblik;

namespace EnergyMonitor.Web.Pricing;

public sealed class EnergyQueryService(IPropertyStore properties, IConsumptionStore consumption, PricingStore pricing)
{
    public const int MaxDays = 90;
    public static (DateOnly From, DateOnly To) ValidateRange(string from, string to)
    {
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            || !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            || t < f || t.DayNumber - f.DayNumber >= MaxDays || t == DateOnly.MaxValue)
            throw new ArgumentException("invalid_range");
        return (f, t);
    }

    public async Task<Property> PropertyAsync(string id, CancellationToken ct) =>
        await properties.GetAsync(id, ct) ?? throw new KeyNotFoundException("property_not_found");

    public async Task<CostResult> CostsAsync(string id, string from, string to, CancellationToken ct)
    {
        var (f, t) = ValidateRange(from, to);
        var property = await PropertyAsync(id, ct);
        var zone = LocalDay.Zone(property.TimeZone);
        var start = LocalDay.StartUtc(f, zone);
        var end = LocalDay.StartUtc(t.AddDays(1), zone);
        var config = await pricing.GetConfigurationAsync(id, ct);
        var intervals = await consumption.GetIntervalsAsync(id, EloverblikMeasurementSource.Id, start, end, ct);
        var prices = new List<SpotPrice>();
        foreach (var area in config.Agreements.Select(a => a.Area).Distinct())
            prices.AddRange(await pricing.GetPricesAsync(area, start, end, ct));
        var imported = await pricing.GetImportedChargesAsync(id, start, end, ct);
        var manualCodes = config.Rates.Select(r => r.Code).ToHashSet();
        // Explicit configured rates take precedence for a code; never add a charge twice.
        var importedRates = imported.Rates.Where(r => !manualCodes.Contains(r.Code)
            && config.TariffMappings.Any(m => m.Code == r.Code && m.Owner == r.Owner && m.Unit == r.Unit && m.Vat == r.Vat
                && m.Component == r.Component && m.FromUtc <= r.FromUtc && m.ToUtc >= r.ToUtc));
        var links = imported.Links.Concat(config.TariffMappings.Select(m => new TariffLink(m.Code, m.Owner, m.Type, m.FromUtc, m.ToUtc, 1))).ToArray();
        return CostCalculator.Calculate(start, end, intervals, config.Agreements, prices, config.Rates.Concat(importedRates).ToArray(), config.RequiredTariffCodes, config.TariffsConfirmed, links);
    }

    /// <summary>"Ca. pris": always a total, with today's Eloverblik charges and the latest registered terms.</summary>
    public async Task<ApproximateEstimate> EstimateAsync(string id, string from, string to, CancellationToken ct)
    {
        var (f, t) = ValidateRange(from, to);
        var property = await PropertyAsync(id, ct);
        var zone = LocalDay.Zone(property.TimeZone);
        var start = LocalDay.StartUtc(f, zone);
        var end = LocalDay.StartUtc(t.AddDays(1), zone);
        var terms = (await pricing.GetConfigurationAsync(id, ct)).Agreements.OrderBy(a => a.FromUtc).LastOrDefault();
        var intervals = await consumption.GetIntervalsAsync(id, EloverblikMeasurementSource.Id, start, end, ct);
        var prices = await pricing.GetPricesAsync(terms?.Area ?? "DK1", start, end, ct);
        var evidence = (await pricing.GetChargeEvidenceAsync(id, ct)) is [var latest, ..] ? latest : (System.Text.Json.JsonElement?)null;
        return ApproximatePricing.Estimate(start, end, intervals, prices, evidence, terms);
    }

    public async Task<object> ConsumptionAsync(string id, string from, string to, string resolution, CancellationToken ct)
    {
        var (f, t) = ValidateRange(from, to);
        var property = await PropertyAsync(id, ct);
        var zone = LocalDay.Zone(property.TimeZone);
        if (resolution is not ("day" or "interval")) throw new ArgumentException("invalid_resolution");
        object items;
        if (resolution == "interval") items = CostCalculator.SelectIntervals(await consumption.GetIntervalsAsync(id,
            EloverblikMeasurementSource.Id, LocalDay.StartUtc(f, zone), LocalDay.StartUtc(t.AddDays(1), zone), ct));
        else
        {
            var days = (await consumption.GetDailyAsync(id, f, t, ct)).ToDictionary(d => d.LocalDate);
            items = LocalDay.Range(f, t).Select(d => days.GetValueOrDefault(d) ?? new DailyConsumption(id, d, null, 0, 0, DayQuality.Missing, DateTimeOffset.MinValue)).ToArray();
        }
        return new { from = f, to = t, unit = "kWh", timeZone = property.TimeZone, resolution, items };
    }

    public async Task<object> BreakdownAsync(string id, string from, string to, CancellationToken ct)
    {
        var result = await CostsAsync(id, from, to, ct);
        var config = await pricing.GetConfigurationAsync(id, ct);
        var prices = new List<SpotPrice>();
        foreach (var area in config.Agreements.Select(a => a.Area).Distinct())
            prices.AddRange(await pricing.GetPricesAsync(area, result.FromUtc, result.ToUtc, ct));
        return new { result, configuration = config, prices, imported = await pricing.GetImportedChargesAsync(id, result.FromUtc, result.ToUtc, ct), sources = await pricing.GetSourcesAsync(id, ct) };
    }

    public async Task<object> StatusAsync(string id, string from, string to, CancellationToken ct) =>
        new { costs = await CostsAsync(id, from, to, ct), sources = await pricing.GetSourcesAsync(id, ct) };

    public async Task<object> OverviewAsync(string from, string to, CancellationToken ct)
    {
        ValidateRange(from, to);
        var items = new List<object>();
        foreach (var p in await properties.ListAsync(ct))
            items.Add(new { p.Id, p.Name, consumption = await ConsumptionAsync(p.Id, from, to, "day", ct), costs = await CostsAsync(p.Id, from, to, ct) });
        return new { currency = "DKK", unit = "kWh", timeZone = Property.DefaultTimeZone, items };
    }

    public async Task<object> CompareAsync(string id, string from, string to, string previousFrom, string previousTo, CancellationToken ct) =>
        new { current = await CostsAsync(id, from, to, ct), previous = await CostsAsync(id, previousFrom, previousTo, ct) };

    public static bool ValidConfiguration(PricingConfiguration c)
    {
        if (c.TariffMappings is null || c.TariffMappings.Count > 100 || c.TariffMappings.Any(m => string.IsNullOrWhiteSpace(m.Code) || string.IsNullOrWhiteSpace(m.Owner)
            || m.Code.Length > 80 || m.ToUtc <= m.FromUtc || m.Unit is not ("kWh" or "day") || m.Vat is not ("Included" or "Excluded" or "Exempt")
            || m.Component is not ("Network" or "Tax" or "Subscription"))) return false;
        if (c.Agreements is null || c.Rates is null || c.RequiredTariffCodes is null || c.Agreements.Count > 100 || c.Rates.Count > 1000) return false;
        var sorted = c.Agreements.OrderBy(a => a.FromUtc).ToArray();
        if (sorted.Any(a => a.Area is not ("DK1" or "DK2") || a.Kind is not ("Spot" or "Fixed")
            || a.ToUtc <= a.FromUtc || a.SettlementMinutes is not (15 or 60 or 1440) || a.VatRate is < 0 or > 1
            || a.DailySubscription < 0 || (a.Kind == "Fixed" && a.FixedPrice is null))) return false;
        if (sorted.Zip(sorted.Skip(1)).Any(p => p.First.ToUtc is null || p.First.ToUtc > p.Second.FromUtc)) return false;
        return c.RequiredTariffCodes.All(code => !string.IsNullOrWhiteSpace(code) && code.Length <= 80)
            && c.RequiredTariffCodes.Distinct().Count() == c.RequiredTariffCodes.Count
            && c.Rates.All(r => !string.IsNullOrWhiteSpace(r.Code) && r.Code.Length <= 80 && r.ToUtc > r.FromUtc
                && r.Unit is "kWh" or "day" && r.Component is "Network" or "Tax" or "Subscription"
                && r.Vat is "Included" or "Excluded" or "Exempt" or "Unknown" && r.FromHour >= 0 && r.ToHour <= 24 && r.ToHour > r.FromHour)
            && !c.Rates.Any(r => c.Rates.Any(s => !ReferenceEquals(r, s) && r.Code == s.Code && r.FromUtc < s.ToUtc && s.FromUtc < r.ToUtc && r.FromHour < s.ToHour && s.FromHour < r.ToHour));
    }
}
