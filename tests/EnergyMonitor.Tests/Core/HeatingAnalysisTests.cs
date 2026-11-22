using EnergyMonitor.Core.Weather;

namespace EnergyMonitor.Tests.Core;

/// <summary>Daily consumption against degree-days: the fitted line and when there is none.</summary>
public sealed class HeatingAnalysisTests
{
    private static readonly DateOnly First = new(2026, 10, 1);

    /// <summary>Days with outdoor temperatures 0, 1, 2 … and the consumption the function gives.</summary>
    private static (DateOnly, double, double, double?)[] Days(int count, Func<double, double> kWh, Func<int, double?>? indoor = null, Func<int, double>? outdoor = null) =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            var outside = outdoor?.Invoke(i) ?? i % 12;
            return (First.AddDays(i), kWh(outside), outside, indoor?.Invoke(i));
        })];

    [Fact]
    public void Line_IsRecoveredExactly_FromConsumptionThatFollowsTheBaseTemperature()
    {
        var result = HeatingAnalysis.Analyse(Days(30, outside => 2 + (0.5 * (17 - outside))));

        Assert.Equal(HeatingResult.Ok, result.Status);
        Assert.False(result.UsesIndoor);
        Assert.Equal(17, result.BaseTemperatureC);
        Assert.Equal((2, 0.5, 1, 30), (Math.Round(result.Model!.BaseKWhPerDay, 9), Math.Round(result.Model.KWhPerDegreeDay, 9), Math.Round(result.Model.RSquared, 9), result.Model.Days));
        var day = result.Days[3];
        Assert.Equal((First.AddDays(3), 3d, 14d), (day.Date, day.OutdoorC, day.DegreeDays));
        Assert.Equal(day.KWh, day.ExpectedKWh!.Value, 9);
        Assert.Null(day.IndoorC);
    }

    [Fact]
    public void DegreeDays_AreNeverBelowZero_SoWarmDaysShowTheBaseLoad()
    {
        // 10 days at 20 °C use only the base load; the colder days add 1 kWh per degree below 17.
        var result = HeatingAnalysis.Analyse(Days(30, outside => 3 + Math.Max(0, 17 - outside), outdoor: i => i < 10 ? 20 : i - 5));

        Assert.All(result.Days.Take(10), d => Assert.Equal(0, d.DegreeDays));
        Assert.Equal(3, result.Model!.BaseKWhPerDay, 9);
        Assert.Equal(1, result.Model.KWhPerDegreeDay, 9);
    }

    [Fact]
    public void RecordedIndoorTemperature_ReplacesTheBase_AndDaysWithoutItAreLeftOut()
    {
        // A house kept at 10 °C: against a base of 17 °C every day would look seven degrees colder than it is.
        var days = Days(30, outside => 1 + (0.4 * Math.Max(0, 10 - outside)), indoor: i => i < 20 ? 10 : null);

        var result = HeatingAnalysis.Analyse(days);

        Assert.True(result.UsesIndoor);
        Assert.Equal(20, result.Days.Count);
        Assert.Equal(20, result.Model!.Days);
        Assert.Equal((1, 0.4), (Math.Round(result.Model.BaseKWhPerDay, 9), Math.Round(result.Model.KWhPerDegreeDay, 9)));
        Assert.Equal((10d, 7d), (result.Days[3].IndoorC, result.Days[3].DegreeDays));
    }

    [Fact]
    public void AFewIndoorDays_AreNotEnough_ToSwitchFromTheBaseTemperature()
    {
        var result = HeatingAnalysis.Analyse(Days(30, outside => 2 + (0.5 * (17 - outside)), indoor: i => i < HeatingAnalysis.MinDays - 1 ? 10 : null));

        Assert.False(result.UsesIndoor);
        Assert.Equal(30, result.Days.Count);
        Assert.All(result.Days, d => Assert.Null(d.IndoorC));
    }

    [Fact]
    public void TooFewDays_OrDaysThatAreAllEquallyCold_GiveNoModel()
    {
        var few = HeatingAnalysis.Analyse(Days(HeatingAnalysis.MinDays - 1, outside => 2 + outside));
        Assert.Equal(HeatingResult.InsufficientData, few.Status);
        Assert.Null(few.Model);
        Assert.Equal(13, few.Days.Count);
        Assert.All(few.Days, d => Assert.Null(d.ExpectedKWh));

        var flat = HeatingAnalysis.Analyse(Days(30, _ => 5, outdoor: _ => 4));
        Assert.Equal(HeatingResult.InsufficientData, flat.Status);

        Assert.Equal(HeatingResult.InsufficientData, HeatingAnalysis.Analyse([]).Status);
    }

    [Fact]
    public void ConsumptionThatDoesNotFollowTheCold_HasALowRSquared()
    {
        var result = HeatingAnalysis.Analyse(Days(30, _ => 0, outdoor: i => i % 12).Select((d, i) => (d.Item1, i % 2 == 0 ? 4d : 8d, d.Item3, d.Item4)));

        Assert.Equal(HeatingResult.Ok, result.Status);
        Assert.InRange(result.Model!.RSquared, 0, 0.1);
    }

    [Fact]
    public void Days_AreReturnedInDateOrder()
    {
        var result = HeatingAnalysis.Analyse(Days(20, outside => 2 + outside).Reverse());

        Assert.Equal(First, result.Days[0].Date);
        Assert.Equal(First.AddDays(19), result.Days[^1].Date);
    }
}
