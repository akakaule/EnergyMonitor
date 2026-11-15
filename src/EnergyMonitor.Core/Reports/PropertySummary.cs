using EnergyMonitor.Core.Alerts;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;

namespace EnergyMonitor.Core.Reports;

public enum PropertyStatus
{
    Normal,
    Observer,
    Alarm,
    DataMissing,
}

public sealed record PropertySummary(
    Property Property,
    PropertyStatus Status,
    DailyConsumption? LatestComplete,
    double? Baseline,
    double? DeviationKWh,
    double? DeviationPercent,
    double? Sum7,
    double? Sum30,
    IReadOnlyList<DateOnly> PendingDays,
    IReadOnlyList<AlertEvent> OpenEvents,
    bool IsStale,
    double? DataAgeHours)
{
    public bool HasBaseline => Baseline is not null;
}

public static class PropertySummaryCalculator
{
    public const int StaleAfterHours = 72;

    public static PropertySummary Calculate(
        Property property,
        IReadOnlyList<DailyConsumption> days,
        IReadOnlyList<AlertEvent> events,
        RuleParams baselineParams,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        var byDate = days.ToDictionary(d => d.LocalDate);
        var latest = days.Where(d => d.IsComplete).MaxBy(d => d.LocalDate);
        var open = events.Where(e => e.PropertyId == property.Id && e.State == AlertState.Open).ToList();
        var yesterday = LocalDay.Of(now, zone).AddDays(-1);

        double? baseline = null, deviation = null, deviationPct = null, ageHours = null;
        var pending = new List<DateOnly>();
        if (latest is not null)
        {
            baseline = Baseline.For(days, latest.LocalDate, baselineParams);
            if (baseline is { } b)
            {
                deviation = Math.Round(latest.KWh!.Value - b, 3);
                deviationPct = b > 0 ? Math.Round(deviation.Value / b * 100, 1) : null;
            }

            ageHours = Math.Round((now - LocalDay.Bounds(latest.LocalDate, zone).EndUtc).TotalHours, 1);
            pending.AddRange(LocalDay.Range(latest.LocalDate.AddDays(1), yesterday));
        }

        var stale = latest is null || ageHours > StaleAfterHours;
        // A heat pump alarm (frost, offline) is about the house itself, so missing meter data must not hide it.
        var status = open.Any(e => e.Severity == AlertSeverity.Alarm && AlertRule.HeatPumpRuleTypes.Contains(e.RuleType)) ? PropertyStatus.Alarm
            : latest is null || open.Any(e => e.RuleType == RuleType.MissingData) ? PropertyStatus.DataMissing
            : open.Any(e => e.Severity == AlertSeverity.Alarm) ? PropertyStatus.Alarm
            : open.Count > 0 ? PropertyStatus.Observer
            : PropertyStatus.Normal;

        return new PropertySummary(
            property, status, latest, baseline, deviation, deviationPct,
            SumEndingAt(byDate, latest?.LocalDate, 7), SumEndingAt(byDate, latest?.LocalDate, 30),
            pending, open, stale, ageHours);
    }

    /// <summary>
    /// Sum of the <paramref name="count"/> consecutive days ending at <paramref name="end"/>, or null unless
    /// every one of them is complete: a total with holes in it is not comparable with anything.
    /// </summary>
    public static double? SumEndingAt(IReadOnlyDictionary<DateOnly, DailyConsumption> byDate, DateOnly? end, int count)
    {
        if (end is null)
        {
            return null;
        }

        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            if (!byDate.TryGetValue(end.Value.AddDays(-i), out var day) || !day.IsComplete)
            {
                return null;
            }

            sum += day.KWh!.Value;
        }

        return Math.Round(sum, 3);
    }
}
