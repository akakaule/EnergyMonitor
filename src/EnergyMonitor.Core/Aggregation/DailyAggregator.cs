using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;

namespace EnergyMonitor.Core.Aggregation;

public static class DailyAggregator
{
    /// <summary>
    /// Summarises one local day from the stored intervals. Coverage is measured against the number of
    /// intervals the day actually has (23, 24 or 25 hours), so a short or long day is complete when every
    /// hour arrived. A day with nothing usable is <see cref="DayQuality.Missing"/> with a null sum.
    /// </summary>
    public static DailyConsumption Aggregate(
        string propertyId,
        DateOnly date,
        TimeZoneInfo zone,
        IEnumerable<ConsumptionInterval> intervals,
        DateTimeOffset now)
    {
        var (start, end) = LocalDay.Bounds(date, zone);
        var inDay = intervals
            .Where(i => i.StartUtc >= start && i.EndUtc <= end && i.EndUtc > i.StartUtc)
            .ToList();

        if (inDay.Count == 0)
        {
            return new DailyConsumption(propertyId, date, null, ExpectedHours(start, end), 0, DayQuality.Missing, now);
        }

        // If the same day exists at several resolutions, use the finest; never mix them.
        var step = inDay.Min(i => i.EndUtc - i.StartUtc);
        var series = inDay.Where(i => i.EndUtc - i.StartUtc == step).ToList();
        var expected = (int)Math.Round((end - start) / step, MidpointRounding.AwayFromZero);
        var usable = series.Where(i => i.HasValue).ToList();
        var received = usable.Count;

        var quality = received == 0 ? DayQuality.Missing
            : received >= expected ? DayQuality.Complete
            : DayQuality.Partial;
        double? sum = received == 0 ? null : Math.Round(usable.Sum(i => i.KWh!.Value), 3);

        return new DailyConsumption(propertyId, date, sum, expected, received, quality, now);
    }

    private static int ExpectedHours(DateTimeOffset start, DateTimeOffset end) =>
        (int)Math.Round((end - start).TotalHours, MidpointRounding.AwayFromZero);
}
