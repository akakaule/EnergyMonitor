using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;

namespace EnergyMonitor.Core.Pricing;

public static class CostCalculator
{
    // A partial finer series supersedes the entire coarser series for that Danish day.
    // Filling gaps from a coarse total would count some energy twice.
    public static IReadOnlyList<ConsumptionInterval> SelectIntervals(IEnumerable<ConsumptionInterval> intervals) =>
        intervals.Where(i => i.EndUtc > i.StartUtc)
            .GroupBy(i => LocalDay.Of(i.StartUtc, LocalDay.Zone(Property.DefaultTimeZone)))
            .SelectMany(g => g.Where(i => i.EndUtc - i.StartUtc == g.Min(x => x.EndUtc - x.StartUtc)))
            .GroupBy(i => (i.StartUtc, i.EndUtc)).Select(g => g.OrderByDescending(i => i.FetchedAtUtc).First())
            .OrderBy(i => i.StartUtc).ToArray();

    public static CostResult Calculate(DateTimeOffset from, DateTimeOffset to,
        IReadOnlyList<ConsumptionInterval> consumption, IReadOnlyList<ElectricityAgreement> agreements,
        IReadOnlyList<SpotPrice> prices, IReadOnlyList<TariffRate> rates,
        IReadOnlyList<string> requiredCodes, bool tariffsConfirmed, IReadOnlyList<TariffLink>? links = null)
    {
        if (to <= from) throw new ArgumentException("invalid_range");
        var zone = LocalDay.Zone(Property.DefaultTimeZone);
        links ??= [];
        var missing = new HashSet<string>();
        var sources = new HashSet<string>();
        var components = new Dictionary<string, decimal> { ["Energy"] = 0, ["Supplier"] = 0,
            ["Network"] = 0, ["Tax"] = 0, ["Subscription"] = 0, ["Vat"] = 0 };
        var selected = SelectIntervals(consumption).Where(i => i.StartUtc < to && i.EndUtc > from).ToArray();
        var boundaries = selected.SelectMany(i => new[] { i.StartUtc, i.EndUtc })
            .Concat(agreements.SelectMany(a => new[] { a.FromUtc, a.ToUtc ?? to }))
            .Concat(prices.SelectMany(p => new[] { p.StartUtc, p.EndUtc }))
            .Concat(rates.SelectMany(r => new[] { r.FromUtc, r.ToUtc }))
            .Concat(links.SelectMany(r => new[] { r.FromUtc, r.ToUtc }))
            .Concat(new[] { from, to }).Where(t => t >= from && t <= to).ToHashSet();
        // Hour boundaries handle tariff bands and Danish-day subscription allocation, including DST.
        for (var t = from; t < to; t = t.AddMinutes(15)) boundaries.Add(t);
        var points = boundaries.Order().ToArray();
        decimal covered = 0, priced = 0, knownKWh = 0;
        var estimated = false;
        for (var n = 0; n < points.Length - 1; n++)
        {
            var start = points[n];
            var end = points[n + 1];
            var duration = (decimal)(end - start).TotalSeconds;
            var active = selected.Where(i => i.StartUtc <= start && i.EndUtc >= end && i.HasValue).ToArray();
            var aa = agreements.Where(a => a.FromUtc <= start && (a.ToUtc is null || a.ToUtc >= end)).ToArray();
            var a = aa.Length == 1 ? aa[0] : null;
            if (a is null) missing.Add(aa.Length > 1 ? "overlapping_agreements" : "agreement");
            var day = LocalDay.Of(start, zone);
            var bounds = LocalDay.Bounds(day, zone);
            decimal DailyAmount(decimal amount) =>
                amount * (decimal)(end - bounds.StartUtc).TotalSeconds / (decimal)(bounds.EndUtc - bounds.StartUtc).TotalSeconds
                - amount * (decimal)(start - bounds.StartUtc).TotalSeconds / (decimal)(bounds.EndUtc - bounds.StartUtc).TotalSeconds;
            void Add(string component, decimal amount, string vat)
            {
                var net = vat == "Included" && a is not null ? amount / (1 + a.VatRate) : amount;
                components[component] = components.GetValueOrDefault(component) + net;
                if (vat is "Included" or "Excluded" && a is not null) components["Vat"] += net * a.VatRate;
                else if (vat != "Exempt") missing.Add("vat");
            }
            if (a?.DailySubscription is { } subscription) Add("Subscription", DailyAmount(subscription), "Excluded");
            else missing.Add("subscription");
            if (!tariffsConfirmed) missing.Add("tariff_inventory");
            var hour = TimeZoneInfo.ConvertTime(start, zone).Hour;
            var applicable = rates.Where(r => r.FromUtc <= start && r.ToUtc >= end && hour >= r.FromHour && hour < r.ToHour).ToArray();
            foreach (var r in applicable.Where(r => r.Unit == "day"))
            {
                if (applicable.Count(x => x.Code == r.Code) != 1) { missing.Add("duplicate_tariff:" + r.Code); continue; }
                if (r.Price is { } p) { Add("Subscription", DailyAmount(p), r.Vat); sources.Add(r.Source); }
                else missing.Add("tariff:" + r.Code);
            }
            if (active.Length != 1) { missing.Add(active.Length > 1 ? "overlapping_consumption" : "consumption"); continue; }
            var i = active[0];
            var kWh = (decimal)i.KWh!.Value * duration / (decimal)(i.EndUtc - i.StartUtc).TotalSeconds;
            covered += duration;
            knownKWh += kWh;
            sources.Add(i.Source);
            if (a is null) continue;
            sources.Add("registered_agreement");
            estimated |= i.Quality == IntervalQuality.Estimated;
            estimated |= rates.Any(r => (r.FromUtc > i.StartUtc && r.FromUtc < i.EndUtc)
                    || (r.ToUtc > i.StartUtc && r.ToUtc < i.EndUtc))
                || agreements.Any(term => term.FromUtc > i.StartUtc && term.FromUtc < i.EndUtc)
                || rates.Any(r => (r.FromHour != 0 || r.ToHour != 24) && (i.EndUtc - i.StartUtc).TotalMinutes > 60);
            var intervalPrices = prices.Where(p => p.Area == a.Area && p.StartUtc < i.EndUtc && p.EndUtc > i.StartUtc).ToArray();
            estimated |= (i.EndUtc - i.StartUtc).TotalMinutes > a.SettlementMinutes
                || (a.Kind == "Spot" && intervalPrices.Select(p => p.DkkPerKWh).Distinct().Count() > 1);
            var settlementStart = a.SettlementMinutes == 1440 ? bounds.StartUtc
                : a.SettlementMinutes == 60 ? new DateTimeOffset(start.UtcDateTime.Date.AddHours(start.UtcDateTime.Hour), TimeSpan.Zero)
                : new DateTimeOffset(start.UtcDateTime.Date.AddHours(start.UtcDateTime.Hour).AddMinutes(start.Minute / 15 * 15), TimeSpan.Zero);
            var settlementEnd = a.SettlementMinutes == 1440 ? bounds.EndUtc : settlementStart.AddMinutes(a.SettlementMinutes);
            var spot = prices.Where(p => p.Area == a.Area && p.StartUtc < settlementEnd && p.EndUtc > settlementStart).OrderBy(p => p.StartUtc).ToArray();
            decimal? spotPrice = null;
            var cursor = settlementStart;
            decimal weighted = 0;
            foreach (var p in spot)
            {
                var ps = p.StartUtc < settlementStart ? settlementStart : p.StartUtc;
                var pe = p.EndUtc > settlementEnd ? settlementEnd : p.EndUtc;
                if (ps != cursor || pe <= ps) { cursor = settlementStart; break; }
                weighted += p.DkkPerKWh * (decimal)(pe - ps).TotalSeconds;
                cursor = pe;
            }
            if (cursor == settlementEnd) spotPrice = weighted / (decimal)(settlementEnd - settlementStart).TotalSeconds;
            var energy = a.Kind == "Fixed" ? a.FixedPrice : spotPrice;
            if (energy is { } price) { Add("Energy", kWh * price, "Excluded"); priced += duration; if (a.Kind == "Spot") sources.Add(spot[0].Source); }
            else missing.Add(a.Kind == "Fixed" ? "fixed_price" : "spot");
            if (a.SupplierMarkup is { } markup) Add("Supplier", kWh * markup, "Excluded");
            else missing.Add("supplier");
            foreach (var code in requiredCodes.Union(applicable.Select(r => r.Code))
                .Union(links.Where(l => l.FromUtc <= start && l.ToUtc >= end).Select(l => l.Code)))
            {
                var rr = applicable.Where(r => r.Code == code).ToArray();
                if (rr.Length != 1) { missing.Add("tariff:" + code); continue; }
                var r = rr[0];
                if (r.Unit == "day") continue;
                if (r.Unit != "kWh" || r.Price is null) { missing.Add("tariff:" + code); continue; }
                Add(r.Component, kWh * r.Price.Value, r.Vat);
                sources.Add(r.Source);
            }
        }
        var subtotal = components.Values.Sum();
        var complete = missing.Count == 0;
        var seconds = (decimal)(to - from).TotalSeconds;
        return new(from, to, "DKK", "kWh", Property.DefaultTimeZone,
            complete ? estimated ? "Estimated" : "Calculated" : covered == 0 ? "Unavailable" : "Partial",
            estimated, covered / seconds, priced / seconds, knownKWh, subtotal, complete ? subtotal : null,
            components, missing.Order().ToArray(), sources.Order().ToArray(),
            "UTC intervals; uniform allocation of coarse readings (estimated); subscriptions per Danish calendar day; VAT per component; rounding only on display. Calculated under registered terms, not invoice reconciled.");
    }
}

