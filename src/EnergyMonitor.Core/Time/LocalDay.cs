namespace EnergyMonitor.Core.Time;

/// <summary>
/// Local calendar day arithmetic. Stored timestamps are UTC; a day runs from local midnight to the next
/// local midnight, which is 23 or 25 hours long on the days daylight saving time changes.
/// </summary>
public static class LocalDay
{
    public static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    public static DateTimeOffset StartUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) Bounds(DateOnly date, TimeZoneInfo zone) =>
        (StartUtc(date, zone), StartUtc(date.AddDays(1), zone));

    public static DateOnly Of(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    public static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // The hour skipped in spring: use the first valid instant after it.
            local = local.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    public static IEnumerable<DateOnly> Range(DateOnly from, DateOnly toInclusive)
    {
        for (var d = from; d <= toInclusive; d = d.AddDays(1))
        {
            yield return d;
        }
    }
}
