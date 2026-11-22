namespace EnergyMonitor.Core.Weather;

/// <summary>One complete day: what was used, how cold it was, and what the model expects for that cold.</summary>
public sealed record HeatingDay(DateOnly Date, double KWh, double OutdoorC, double? IndoorC, double DegreeDays, double? ExpectedKWh);

/// <summary>
/// The straight line through the days: <c>kWh = BaseKWhPerDay + KWhPerDegreeDay × degree-days</c>.
/// <see cref="RSquared"/> is the share of the day-to-day variation the line explains (0–1).
/// </summary>
public sealed record HeatingModel(double BaseKWhPerDay, double KWhPerDegreeDay, double RSquared, int Days);

public sealed record HeatingResult(string Status, double BaseTemperatureC, bool UsesIndoor, HeatingModel? Model, IReadOnlyList<HeatingDay> Days)
{
    public const string Ok = "Ok";

    /// <summary>Too few days, or days that are all equally cold: no line can be drawn.</summary>
    public const string InsufficientData = "InsufficientData";
}

/// <summary>What a day's outdoor temperature leads one to expect it to use.</summary>
public sealed record WeatherExpectation(double ExpectedKWh, double OutdoorC);

/// <summary>
/// How a property's daily electricity use follows the cold. A day's degree-days are how many degrees
/// colder it was outside than inside, never below zero: with a recorded indoor temperature (a heat pump
/// that is read every 15 minutes) the real difference, otherwise the difference to a fixed base temperature.
/// The consumption is the whole metering point's, so everything else in the house ends up in the base load.
/// </summary>
public static class HeatingAnalysis
{
    /// <summary>The base of Danish degree-days: 17 °C indoors without heating.</summary>
    public const double DefaultBaseC = 17;

    /// <summary>Fewer days than this give no model.</summary>
    public const int MinDays = 14;

    /// <summary>
    /// What <paramref name="date"/> should have used, judged from the days before it only: a day never
    /// takes part in its own expectation. Null when the earlier days give no dependable line (too few,
    /// a weak fit, or colder days that do not cost more), or when the day lacks the temperature the line
    /// is measured against.
    /// </summary>
    public static WeatherExpectation? Expect(
        IReadOnlyCollection<(DateOnly Date, double KWh, double OutdoorC, double? IndoorC)> source, DateOnly date,
        double baseC, int minDays, double minRSquared)
    {
        var earlier = Analyse(source.Where(d => d.Date < date), baseC);
        if (earlier.Model is not { } model || !IsDependable(model, minDays, minRSquared) || !source.Any(d => d.Date == date))
        {
            return null;
        }

        var day = source.First(d => d.Date == date);
        if (earlier.UsesIndoor && day.IndoorC is null)
        {
            return null;
        }

        var degreeDays = Math.Max(0, (earlier.UsesIndoor ? day.IndoorC!.Value : baseC) - day.OutdoorC);
        return new(model.BaseKWhPerDay + (model.KWhPerDegreeDay * degreeDays), day.OutdoorC);
    }

    /// <summary>True when a line is good enough to judge single days by.</summary>
    public static bool IsDependable(HeatingModel model, int minDays, double minRSquared) =>
        model.Days >= minDays && model.RSquared >= minRSquared && model.KWhPerDegreeDay > 0;

    public static HeatingResult Analyse(IEnumerable<(DateOnly Date, double KWh, double OutdoorC, double? IndoorC)> source, double baseC = DefaultBaseC)
    {
        var all = source.OrderBy(d => d.Date).ToArray();
        // Either every day is measured against the recorded indoor temperature, or none is: mixing the two
        // would compare a house kept at 10 °C with a base of 17 °C.
        var usesIndoor = all.Count(d => d.IndoorC is not null) >= MinDays;
        var days = usesIndoor ? [.. all.Where(d => d.IndoorC is not null)] : all;
        var degreeDays = days.Select(d => Math.Max(0, (usesIndoor ? d.IndoorC!.Value : baseC) - d.OutdoorC)).ToArray();

        HeatingModel? model = null;
        if (days.Length >= MinDays)
        {
            var meanX = degreeDays.Average();
            var meanY = days.Average(d => d.KWh);
            double sxx = 0, sxy = 0, syy = 0;
            for (var i = 0; i < days.Length; i++)
            {
                sxx += (degreeDays[i] - meanX) * (degreeDays[i] - meanX);
                sxy += (degreeDays[i] - meanX) * (days[i].KWh - meanY);
                syy += (days[i].KWh - meanY) * (days[i].KWh - meanY);
            }

            // Days that are all equally cold say nothing about what a degree costs.
            if (sxx > 1e-9)
            {
                var slope = sxy / sxx;
                model = new(meanY - (slope * meanX), slope, syy > 1e-9 ? sxy * sxy / (sxx * syy) : 0, days.Length);
            }
        }

        return new(model is null ? HeatingResult.InsufficientData : HeatingResult.Ok, baseC, usesIndoor, model,
            [.. days.Select((d, i) => new HeatingDay(d.Date, d.KWh, d.OutdoorC, usesIndoor ? d.IndoorC : null, degreeDays[i],
                model is null ? null : model.BaseKWhPerDay + (model.KWhPerDegreeDay * degreeDays[i])))]);
    }
}
