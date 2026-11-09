using EnergyMonitor.Core.Model;

namespace EnergyMonitor.Core.Alerts;

public static class Baseline
{
    public static double Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("Median of an empty set is undefined.", nameof(values));
        }

        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>
    /// Median of the most recent complete days strictly before <paramref name="analysisDate"/>.
    /// Null while fewer than <see cref="RuleParams.MinBaselineDays"/> complete days exist.
    /// </summary>
    public static double? For(IEnumerable<DailyConsumption> days, DateOnly analysisDate, RuleParams p)
    {
        var window = days
            .Where(d => d.IsComplete && d.LocalDate < analysisDate)
            .OrderByDescending(d => d.LocalDate)
            .Take(p.BaselineDays)
            .Select(d => d.KWh!.Value)
            .ToArray();

        return window.Length < p.MinBaselineDays ? null : Math.Round(Median(window), 3);
    }
}
