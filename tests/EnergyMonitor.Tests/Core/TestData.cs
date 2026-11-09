using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;

namespace EnergyMonitor.Tests.Core;

internal static class TestData
{
    public static readonly TimeZoneInfo Zone = LocalDay.Zone(Property.DefaultTimeZone);

    public static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Property Sommerhus(bool monitoring = true) =>
        new("sommerhus", "Sommerhus", "571313100000000001", monitoring, Property.DefaultTimeZone, Created);

    public static Property Hjem() =>
        new("hjem", "Hjem", "571313100000000002", true, Property.DefaultTimeZone, Created);

    public static DailyConsumption Day(DateOnly date, double? kWh, DayQuality quality = DayQuality.Complete, string propertyId = "sommerhus") =>
        new(propertyId, date, kWh, 24, quality == DayQuality.Complete ? 24 : quality == DayQuality.Partial ? 10 : 0, quality, Created);

    /// <summary><paramref name="count"/> complete days ending the day before <paramref name="before"/>.</summary>
    public static List<DailyConsumption> History(DateOnly before, int count, double kWh = 8, string propertyId = "sommerhus") =>
        [.. Enumerable.Range(1, count).Select(i => Day(before.AddDays(-i), kWh, propertyId: propertyId)).Reverse()];

    public static List<AlertRule> DefaultRules(string propertyId = "sommerhus") =>
        [.. AlertRule.PropertyRuleTypes.Select(t => AlertRule.Default(propertyId, t, Created))];

    /// <summary>Hourly intervals covering one local day, however many hours it has.</summary>
    public static List<ConsumptionInterval> HourlyDay(DateOnly date, double kWhPerHour = 0.5, string propertyId = "sommerhus")
    {
        var (start, end) = LocalDay.Bounds(date, Zone);
        var list = new List<ConsumptionInterval>();
        for (var t = start; t < end; t = t.AddHours(1))
        {
            list.Add(new ConsumptionInterval(propertyId, "eloverblik", t, t.AddHours(1), kWhPerHour, "PT1H", IntervalQuality.Measured, Created));
        }

        return list;
    }

    /// <summary>Noon UTC on the given local date: safely inside the day in Copenhagen.</summary>
    public static DateTimeOffset Noon(DateOnly date) => new(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
}
