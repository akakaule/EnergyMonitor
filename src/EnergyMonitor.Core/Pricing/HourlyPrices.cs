namespace EnergyMonitor.Core.Pricing;

/// <summary>
/// The price of one kWh in one local clock hour, DKK. The parts are excl. VAT; <see cref="Total"/> is incl. VAT.
/// <see cref="Spot"/> is null when a quarter of the hour has no day-ahead price.
/// </summary>
public sealed record HourPrice(DateTimeOffset StartUtc, DateTimeOffset EndUtc, decimal? Spot, decimal? Energy,
    decimal Supplier, decimal Network, decimal Tax, decimal? Vat, decimal? Total);

/// <summary>A run of consecutive hours and the average of their totals.</summary>
public sealed record PriceWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc, decimal Average);

public sealed record DayPrices(DateOnly Date, string Status, decimal? Average, decimal? Min, decimal? Max,
    PriceWindow? Cheapest, PriceWindow? MostExpensive, int NegativeHours, IReadOnlyList<HourPrice> Hours)
{
    public const string Complete = "Complete";
    public const string Partial = "Partial";
    public const string Pending = "Pending";
}

/// <summary>
/// What a kWh costs hour by hour on a local calendar day: the day-ahead price (or the fixed price), the
/// supplier's markup, the tariffs and taxes that apply in that hour, and VAT. Subscriptions are left out:
/// they do not depend on when the electricity is used.
/// </summary>
public static class HourlyPrices
{
    public const decimal DefaultVatRate = 0.25m;
    public const int MinWindowHours = 1;
    public const int MaxWindowHours = 6;

    /// <param name="prices">Day-ahead prices; those of another area than the agreement's are ignored.</param>
    /// <param name="terms">The agreement in force. Without one: a spot agreement with no markup and 25 % VAT.</param>
    /// <param name="rates">Tariffs per kWh. Rates per day (subscriptions) and rates without a price are ignored.</param>
    /// <param name="notBefore">
    /// When set, the cheapest and most expensive periods are looked for among the hours that have not ended
    /// by then ("the rest of the day"). The average, minimum and maximum always cover the whole day.
    /// </param>
    public static DayPrices ForDay(DateOnly date, TimeZoneInfo zone, IReadOnlyList<SpotPrice> prices,
        ElectricityAgreement? terms, IReadOnlyList<TariffRate> rates, int windowHours, DateTimeOffset? notBefore = null)
    {
        var (dayStart, dayEnd) = Time.LocalDay.Bounds(date, zone);
        var area = terms?.Area;
        var spot = prices.Where(p => (area is null || p.Area == area) && p.StartUtc < dayEnd && p.EndUtc > dayStart)
            .OrderBy(p => p.StartUtc).ToArray();
        var vatRate = terms?.VatRate ?? DefaultVatRate;
        var hours = new List<HourPrice>();
        // A day is 23 or 25 hours long when daylight saving time changes.
        for (var start = dayStart; start < dayEnd; start = start.AddHours(1))
        {
            var end = start.AddHours(1) < dayEnd ? start.AddHours(1) : dayEnd;
            var spotPrice = Average(spot, start, end);
            var energy = terms?.Kind == "Fixed" ? terms.FixedPrice : spotPrice;
            var supplier = terms?.SupplierMarkup ?? 0;
            decimal network = 0, tax = 0, tariffVat = 0;
            var localHour = TimeZoneInfo.ConvertTime(start, zone).Hour;
            foreach (var r in rates.Where(r => r.Unit == "kWh" && r.Price is not null && r.FromUtc <= start && r.ToUtc >= end
                && localHour >= r.FromHour && localHour < r.ToHour))
            {
                var net = r.Vat == "Included" ? r.Price!.Value / (1 + vatRate) : r.Price!.Value;
                if (r.Component == "Tax") tax += net;
                else network += net;
                if (r.Vat != "Exempt") tariffVat += net * vatRate;
            }

            decimal? vat = energy is { } e ? ((e + supplier) * vatRate) + tariffVat : null;
            hours.Add(new(start, end, spotPrice, energy, supplier, network, tax, vat,
                energy is { } known ? known + supplier + network + tax + vat!.Value : null));
        }

        var priced = hours.Count(h => h.Total is not null);
        var status = priced == hours.Count ? DayPrices.Complete : priced == 0 ? DayPrices.Pending : DayPrices.Partial;
        if (status != DayPrices.Complete)
        {
            return new(date, status, null, null, null, null, null, hours.Count(h => h.Spot < 0), hours);
        }

        var totals = hours.Select(h => h.Total!.Value).ToArray();
        var remaining = notBefore is { } t ? hours.Where(h => h.EndUtc > t).ToArray() : [.. hours];
        var length = Math.Clamp(windowHours, MinWindowHours, MaxWindowHours);
        return new(date, status, totals.Average(), totals.Min(), totals.Max(),
            Window(remaining, length, cheapest: true), Window(remaining, length, cheapest: false),
            hours.Count(h => h.Spot < 0), hours);
    }

    /// <summary>The time-weighted average over [start, end), or null unless the prices cover all of it.</summary>
    private static decimal? Average(SpotPrice[] ordered, DateTimeOffset start, DateTimeOffset end)
    {
        var cursor = start;
        decimal weighted = 0;
        foreach (var p in ordered)
        {
            if (p.EndUtc <= cursor) continue;
            if (p.StartUtc > cursor) break;
            var until = p.EndUtc < end ? p.EndUtc : end;
            weighted += p.DkkPerKWh * (decimal)(until - cursor).TotalSeconds;
            cursor = until;
            if (cursor == end) return weighted / (decimal)(end - start).TotalSeconds;
        }

        return null;
    }

    /// <summary>The earliest run of <paramref name="length"/> hours with the lowest (or highest) average.</summary>
    private static PriceWindow? Window(HourPrice[] hours, int length, bool cheapest)
    {
        PriceWindow? best = null;
        for (var i = 0; i + length <= hours.Length; i++)
        {
            var average = hours.Skip(i).Take(length).Average(h => h.Total!.Value);
            if (best is null || (cheapest ? average < best.Average : average > best.Average))
            {
                best = new(hours[i].StartUtc, hours[i + length - 1].EndUtc, average);
            }
        }

        return best;
    }
}
