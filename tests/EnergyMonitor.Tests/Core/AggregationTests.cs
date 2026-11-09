using EnergyMonitor.Core.Aggregation;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;

namespace EnergyMonitor.Tests.Core;

public class AggregationTests
{
    private static readonly DateOnly Ordinary = new(2026, 11, 6);
    private static readonly DateOnly SpringForward = new(2026, 3, 29);
    private static readonly DateOnly FallBack = new(2026, 10, 25);

    [Fact]
    public void Bounds_OrdinaryDay_Is24HoursFromLocalMidnight()
    {
        var (start, end) = LocalDay.Bounds(Ordinary, TestData.Zone);

        Assert.Equal(new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(24, (end - start).TotalHours);
    }

    [Fact]
    public void Bounds_DstDays_Are23And25Hours()
    {
        var spring = LocalDay.Bounds(SpringForward, TestData.Zone);
        var fall = LocalDay.Bounds(FallBack, TestData.Zone);

        Assert.Equal(23, (spring.EndUtc - spring.StartUtc).TotalHours);
        Assert.Equal(25, (fall.EndUtc - fall.StartUtc).TotalHours);
    }

    [Fact]
    public void Of_AssignsUtcInstantToLocalDate()
    {
        // 23:30 UTC on 5 November is already 00:30 on 6 November in Copenhagen.
        Assert.Equal(Ordinary, LocalDay.Of(new DateTimeOffset(2026, 11, 5, 23, 30, 0, TimeSpan.Zero), TestData.Zone));
        Assert.Equal(Ordinary.AddDays(-1), LocalDay.Of(new DateTimeOffset(2026, 11, 5, 22, 30, 0, TimeSpan.Zero), TestData.Zone));
    }

    [Theory]
    [InlineData(2026, 3, 29, 23)]
    [InlineData(2026, 10, 25, 25)]
    [InlineData(2026, 11, 6, 24)]
    public void Aggregate_FullDay_IsCompleteWhateverItsLength(int y, int m, int d, int hours)
    {
        var date = new DateOnly(y, m, d);

        var day = DailyAggregator.Aggregate("sommerhus", date, TestData.Zone, TestData.HourlyDay(date, 0.5), TestData.Created);

        Assert.Equal(DayQuality.Complete, day.Quality);
        Assert.Equal(hours, day.ExpectedIntervals);
        Assert.Equal(hours, day.ReceivedIntervals);
        Assert.Equal(hours * 0.5, day.KWh);
    }

    [Fact]
    public void Aggregate_NoIntervals_IsMissingWithNullSum_NotZero()
    {
        var day = DailyAggregator.Aggregate("sommerhus", Ordinary, TestData.Zone, [], TestData.Created);

        Assert.Equal(DayQuality.Missing, day.Quality);
        Assert.Null(day.KWh);
        Assert.Equal(24, day.ExpectedIntervals);
        Assert.Equal(0, day.ReceivedIntervals);
    }

    [Fact]
    public void Aggregate_SomeHours_IsPartial()
    {
        var intervals = TestData.HourlyDay(Ordinary, 1).Take(10);

        var day = DailyAggregator.Aggregate("sommerhus", Ordinary, TestData.Zone, intervals, TestData.Created);

        Assert.Equal(DayQuality.Partial, day.Quality);
        Assert.Equal(10, day.ReceivedIntervals);
        Assert.Equal(10, day.KWh);
    }

    [Fact]
    public void Aggregate_HoursFlaggedMissingOrIncomplete_DoNotCount()
    {
        var intervals = TestData.HourlyDay(Ordinary, 1);
        intervals[3] = intervals[3] with { KWh = null, Quality = IntervalQuality.Missing };
        intervals[4] = intervals[4] with { Quality = IntervalQuality.Incomplete };

        var day = DailyAggregator.Aggregate("sommerhus", Ordinary, TestData.Zone, intervals, TestData.Created);

        Assert.Equal(DayQuality.Partial, day.Quality);
        Assert.Equal(22, day.ReceivedIntervals);
        Assert.Equal(22, day.KWh);
    }

    [Fact]
    public void Aggregate_AllHoursFlaggedMissing_IsMissing()
    {
        var intervals = TestData.HourlyDay(Ordinary, 1)
            .Select(i => i with { KWh = null, Quality = IntervalQuality.Missing });

        var day = DailyAggregator.Aggregate("sommerhus", Ordinary, TestData.Zone, intervals, TestData.Created);

        Assert.Equal(DayQuality.Missing, day.Quality);
        Assert.Null(day.KWh);
    }

    [Fact]
    public void Aggregate_RealZeroConsumption_IsCompleteZero()
    {
        var day = DailyAggregator.Aggregate("sommerhus", Ordinary, TestData.Zone, TestData.HourlyDay(Ordinary, 0), TestData.Created);

        Assert.Equal(DayQuality.Complete, day.Quality);
        Assert.Equal(0, day.KWh);
    }

    [Fact]
    public void Aggregate_DailyResolutionSource_IsOneExpectedInterval_NoFabricatedHours()
    {
        var (start, end) = LocalDay.Bounds(Ordinary, TestData.Zone);
        ConsumptionInterval[] intervals = [new("sommerhus", "eloverblik", start, end, 7.25, "P1D", IntervalQuality.Measured, TestData.Created)];

        var day = DailyAggregator.Aggregate("sommerhus", Ordinary, TestData.Zone, intervals, TestData.Created);

        Assert.Equal(DayQuality.Complete, day.Quality);
        Assert.Equal(1, day.ExpectedIntervals);
        Assert.Equal(7.25, day.KWh);
    }

    [Fact]
    public void Aggregate_IgnoresIntervalsOutsideTheDay()
    {
        var intervals = TestData.HourlyDay(Ordinary, 1).Concat(TestData.HourlyDay(Ordinary.AddDays(1), 5));

        var day = DailyAggregator.Aggregate("sommerhus", Ordinary, TestData.Zone, intervals, TestData.Created);

        Assert.Equal(24, day.KWh);
    }
}
