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

    [Fact]
    public void Expectation_ComesFromTheEarlierDaysOnly_SoADayNeverExplainsItself()
    {
        // 40 days on the line, then a day at 2 °C that used far too little, and a later day that would bend the line.
        var days = Days(40, outside => 2 + (0.5 * (17 - outside))).ToList();
        var day = First.AddDays(40);
        days.Add((day, 1, 2, null));
        days.Add((day.AddDays(1), 500, 2, null));

        var expectation = HeatingAnalysis.Expect(days, day, 17, 30, 0.5);

        Assert.Equal(9.5, expectation!.ExpectedKWh, 9);
        Assert.Equal(2, expectation.OutdoorC);
    }

    [Fact]
    public void Expectation_IsWithheld_WhenTheEarlierDaysGiveNoDependableLine()
    {
        var good = Days(40, outside => 2 + (0.5 * (17 - outside))).ToList();
        var day = First.AddDays(40);
        good.Add((day, 5, 3, null));
        Assert.NotNull(HeatingAnalysis.Expect(good, day, 17, 30, 0.5));

        Assert.Null(HeatingAnalysis.Expect(good, day, 17, 41, 0.5)); // too few days
        Assert.Null(HeatingAnalysis.Expect(good, day.AddDays(5), 17, 30, 0.5)); // the day itself is not there

        var unrelated = Days(40, _ => 0).Select((d, i) => (d.Item1, i % 2 == 0 ? 4d : 8d, d.Item3, d.Item4)).ToList();
        unrelated.Add((day, 5, 3, null));
        Assert.Null(HeatingAnalysis.Expect(unrelated, day, 17, 30, 0.5)); // the cold explains nothing

        var backwards = Days(40, outside => 2 + (0.5 * outside)).ToList(); // colder days use less
        backwards.Add((day, 5, 3, null));
        Assert.Null(HeatingAnalysis.Expect(backwards, day, 17, 30, 0.5));
    }

    [Fact]
    public void Expectation_NeedsTheIndoorTemperatureOfTheDay_WhenTheLineIsMeasuredAgainstIt()
    {
        var days = Days(40, outside => 1 + (0.4 * Math.Max(0, 10 - outside)), indoor: _ => 10).ToList();
        var day = First.AddDays(40);
        days.Add((day, 3, 0, null));
        Assert.Null(HeatingAnalysis.Expect(days, day, 17, 30, 0.5));

        days[^1] = (day, 3, 0, 12);
        Assert.Equal(1 + (0.4 * 12), HeatingAnalysis.Expect(days, day, 17, 30, 0.5)!.ExpectedKWh, 9);
    }
}
