using EnergyMonitor.Core.Alerts;
using EnergyMonitor.Core.Model;

namespace EnergyMonitor.Tests.Core;

public class RuleEngineTests
{
    private static readonly DateOnly D = new(2026, 11, 6);

    private static RuleEvaluation Evaluate(
        List<DailyConsumption> days,
        IReadOnlyList<AlertEvent>? existing = null,
        Property? property = null,
        List<AlertRule>? rules = null,
        DateTimeOffset? now = null) =>
        RuleEngine.EvaluateProperty(
            property ?? TestData.Sommerhus(), rules ?? TestData.DefaultRules(), days, existing ?? [],
            D.AddDays(-7), now ?? TestData.Noon(D.AddDays(1)), TestData.Zone);

    /// <summary>Applies an evaluation the way the store would, giving the next run's existing events.</summary>
    private static List<AlertEvent> Apply(IReadOnlyList<AlertEvent> existing, RuleEvaluation result)
    {
        var map = existing.ToDictionary(e => e.Id);
        foreach (var e in result.Created.Concat(result.Updated))
        {
            map[e.Id] = e;
        }

        return [.. map.Values];
    }

    [Fact]
    public void Median_OddAndEvenCounts()
    {
        Assert.Equal(3, Baseline.Median([5, 1, 3]));
        Assert.Equal(2.5, Baseline.Median([4, 1, 2, 3]));
    }

    [Fact]
    public void Baseline_ExcludesTheAnalysedDay_AndUsesTheLatest14CompleteDays()
    {
        var days = TestData.History(D, 14, 8);
        days.AddRange(TestData.History(D.AddDays(-14), 10, 100)); // older than the window
        days.Add(TestData.Day(D, 0.1));
        days.Add(TestData.Day(D.AddDays(-3), 500, DayQuality.Partial) with { LocalDate = D.AddDays(-30) });

        Assert.Equal(8, Baseline.For(days, D, new RuleParams()));
    }

    [Fact]
    public void Baseline_NeedsSevenCompleteDays()
    {
        Assert.Null(Baseline.For(TestData.History(D, 6), D, new RuleParams()));
        Assert.NotNull(Baseline.For(TestData.History(D, 7), D, new RuleParams()));
    }

    [Fact]
    public void LowDay_With14DayBaseline_RaisesExactlyOneObserver()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 1.2));

        var result = Evaluate(days);

        var e = Assert.Single(result.Created);
        Assert.Empty(result.Updated);
        Assert.Equal(RuleType.LowConsumption, e.RuleType);
        Assert.Equal(AlertSeverity.Observer, e.Severity);
        Assert.Equal(AlertState.Open, e.State);
        Assert.Equal(D, e.FirstLocalDate);
        Assert.Equal(1.2, e.Observed);
        Assert.Equal(8, e.Baseline);
        Assert.Equal(4, e.Threshold);
        Assert.Contains("1,2 kWh", e.Explanation);
        Assert.Contains("8,0 kWh", e.Explanation);
        Assert.Equal(50, e.ParamsSnapshot.ThresholdPercent);
    }

    [Fact]
    public void RepeatedEvaluation_OfTheSameData_CreatesNothingMore()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 1.2));
        var first = Evaluate(days);
        var existing = Apply([], first);

        for (var i = 0; i < 3; i++)
        {
            var again = Evaluate(days, existing);
            Assert.Empty(again.Created);
            Assert.Empty(again.Updated);
        }
    }

    [Fact]
    public void SecondConsecutiveLowDay_EscalatesTheSameEventToAlarm()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 1.2));
        var existing = Apply([], Evaluate(days));

        days.Add(TestData.Day(D.AddDays(1), 1.0));
        var result = Evaluate(days, existing, now: TestData.Noon(D.AddDays(2)));

        Assert.Empty(result.Created);
        var e = Assert.Single(result.Updated);
        Assert.Equal(D, e.FirstLocalDate);
        Assert.Equal(D.AddDays(1), e.LastLocalDate);
        Assert.Equal(AlertSeverity.Alarm, e.Severity);
        Assert.Equal(1.0, e.Observed);
        Assert.Contains("2 døgn i træk", e.Explanation);
    }

    [Fact]
    public void NormalDayAfterLowDay_ClosesTheEvent_AndALaterLowDayStartsANewObserver()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 1.2));
        var existing = Apply([], Evaluate(days));

        days.Add(TestData.Day(D.AddDays(1), 8));
        var closing = Evaluate(days, existing);
        existing = Apply(existing, closing);
        Assert.Equal(AlertState.Closed, Assert.Single(closing.Updated).State);

        days.Add(TestData.Day(D.AddDays(2), 1.0));
        var later = Evaluate(days, existing);

        var e = Assert.Single(later.Created);
        Assert.Equal(D.AddDays(2), e.FirstLocalDate);
        Assert.Equal(AlertSeverity.Observer, e.Severity);
    }

    [Fact]
    public void ClosedEvent_IsNotReopenedForTheSameDay()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 1.2));
        var created = Assert.Single(Evaluate(days).Created);
        List<AlertEvent> existing = [created with { State = AlertState.Closed, ClosedAtUtc = TestData.Created }];

        var result = Evaluate(days, existing);

        Assert.Empty(result.Created);
        Assert.Empty(result.Updated);
    }

    [Fact]
    public void PartialOrMissingLowDay_RaisesNoLowAlarm()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 0.4, DayQuality.Partial));
        days.Add(TestData.Day(D.AddDays(1), null, DayQuality.Missing));

        var result = Evaluate(days, now: TestData.Noon(D.AddDays(1)));

        Assert.DoesNotContain(result.Created, e => e.RuleType == RuleType.LowConsumption);
    }

    [Fact]
    public void InsufficientHistory_TurnsRelativeRulesOff()
    {
        var days = TestData.History(D, 6, 8);
        days.Add(TestData.Day(D, 0.1));

        Assert.Empty(Evaluate(days).Created);
    }

    [Fact]
    public void BaselineBelowThreeKWh_TurnsRelativeRulesOff()
    {
        var days = TestData.History(D, 14, 2.5);
        days.Add(TestData.Day(D, 0.1));
        days.Add(TestData.Day(D.AddDays(1), 20));

        Assert.Empty(Evaluate(days, now: TestData.Noon(D.AddDays(2))).Created);
    }

    [Fact]
    public void PausedMonitoring_RaisesNoConsumptionAlarms()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 0.1));

        Assert.Empty(Evaluate(days, property: TestData.Sommerhus(monitoring: false)).Created);
    }

    [Fact]
    public void DisabledRule_IsNotEvaluated()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 0.1));
        var rules = TestData.DefaultRules().Select(r => r.Type == RuleType.LowConsumption ? r with { Enabled = false } : r).ToList();

        Assert.Empty(Evaluate(days, rules: rules).Created);
    }

    [Fact]
    public void ConfiguredThreshold_IsUsed()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 5)); // 62.5 % of the median: normal at 50 %, low at 70 %
        var rules = TestData.DefaultRules()
            .Select(r => r.Type == RuleType.LowConsumption ? r with { Params = r.Params with { ThresholdPercent = 70 } } : r).ToList();

        Assert.Empty(Evaluate(days).Created);
        Assert.Single(Evaluate(days, rules: rules).Created);
    }

    [Fact]
    public void HighDay_RaisesObserver_AndDoesNotEscalate()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 20));
        var first = Evaluate(days);
        var e = Assert.Single(first.Created);
        Assert.Equal(RuleType.HighConsumption, e.RuleType);
        Assert.Equal(AlertSeverity.Observer, e.Severity);

        days.Add(TestData.Day(D.AddDays(1), 40));
        var second = Evaluate(days, Apply([], first), now: TestData.Noon(D.AddDays(2)));

        Assert.Equal(AlertSeverity.Observer, Assert.Single(second.Updated).Severity);
    }

    [Fact]
    public void DaysBeforeTheEvaluationWindow_AreNotAnalysed()
    {
        var days = TestData.History(D.AddDays(-20), 14, 8);
        days.Add(TestData.Day(D.AddDays(-20), 0.1)); // an old low day, e.g. seen during the initial backfill
        days.AddRange(TestData.History(D.AddDays(1), 20, 8).Where(d => d.LocalDate > D.AddDays(-20)));

        Assert.Empty(Evaluate(days).Created);
    }

    [Fact]
    public void NoCompleteDayFor72Hours_RaisesDataMissing_NotALowAlarm()
    {
        var days = TestData.History(D.AddDays(1), 14, 8); // latest complete day is D
        var now = TestData.Noon(D.AddDays(4));            // D ended more than 72 h ago

        var result = Evaluate(days, now: now);

        var e = Assert.Single(result.Created);
        Assert.Equal(RuleType.MissingData, e.RuleType);
        Assert.Equal(D.AddDays(1), e.FirstLocalDate);
        Assert.Contains("ikke en forbrugsalarm", e.Explanation);
    }

    [Fact]
    public void DataDelayedLessThan72Hours_IsNotAnAlarm()
    {
        var days = TestData.History(D.AddDays(1), 14, 8);

        Assert.Empty(Evaluate(days, now: TestData.Noon(D.AddDays(3))).Created);
    }

    [Fact]
    public void DataMissing_IsRaisedOnce_AndClosesWhenANewCompleteDayArrives()
    {
        var days = TestData.History(D.AddDays(1), 14, 8);
        var now = TestData.Noon(D.AddDays(4));
        var existing = Apply([], Evaluate(days, now: now));

        Assert.Empty(Evaluate(days, existing, now: now.AddHours(9)).Created);

        days.AddRange([TestData.Day(D.AddDays(1), 8), TestData.Day(D.AddDays(2), 8), TestData.Day(D.AddDays(3), 8)]);
        var result = Evaluate(days, existing, now: now.AddHours(9));

        Assert.Empty(result.Created);
        Assert.Equal(AlertState.Closed, Assert.Single(result.Updated).State);
    }

    [Fact]
    public void PropertyWithNoDataAtAll_RaisesDataMissingAfter72Hours()
    {
        var property = TestData.Sommerhus() with { CreatedAt = TestData.Noon(D) };

        Assert.Empty(Evaluate([], property: property, now: TestData.Noon(D.AddDays(2))).Created);
        Assert.Equal(RuleType.MissingData, Assert.Single(Evaluate([], property: property, now: TestData.Noon(D.AddDays(4))).Created).RuleType);
    }

    [Fact]
    public void NeedsNotification_OnceWhenRaised_AndAgainOnlyWhenSeverityRises()
    {
        var days = TestData.History(D, 14, 8);
        days.Add(TestData.Day(D, 1.2));
        var e = Assert.Single(Evaluate(days).Created);

        Assert.True(RuleEngine.NeedsNotification(e));
        var notified = e with { LastNotifiedSeverity = AlertSeverity.Observer, LastNotifiedAtUtc = TestData.Created };
        Assert.False(RuleEngine.NeedsNotification(notified));
        Assert.True(RuleEngine.NeedsNotification(notified with { Severity = AlertSeverity.Alarm }));
        Assert.False(RuleEngine.NeedsNotification(e with { State = AlertState.Closed }));
    }

    private static SyncRun Run(int hoursAgo, SyncStatus status, string? error = null) =>
        new(Guid.NewGuid().ToString("N"), "eloverblik", TestData.Noon(D).AddHours(-hoursAgo), TestData.Noon(D).AddHours(-hoursAgo).AddMinutes(1), status, 0, error);

    private static RuleEvaluation Source(SyncRun[] runs, DateTimeOffset? expires = null, IReadOnlyList<AlertEvent>? existing = null) =>
        RuleEngine.EvaluateSource(runs, expires, existing ?? [], new RuleParams(), TestData.Noon(D), TestData.Zone);

    [Fact]
    public void ThreeConsecutiveFailedSyncs_RaiseOneTechnicalAlarm()
    {
        SyncRun[] runs = [Run(1, SyncStatus.Failed, "http_503"), Run(10, SyncStatus.Failed, "http_503"), Run(25, SyncStatus.Failed, "http_429"), Run(34, SyncStatus.Succeeded)];

        var result = Source(runs);

        var e = Assert.Single(result.Created);
        Assert.Equal(RuleType.SourceFailure, e.RuleType);
        Assert.Equal(AlertEvent.SystemPropertyId, e.PropertyId);
        Assert.Contains("http_503", e.Explanation);
        Assert.Empty(Source(runs, existing: result.Created).Created);
    }

    [Fact]
    public void TwoFailures_AreNotYetAnAlarm_ButARejectedTokenIsImmediately()
    {
        Assert.Empty(Source([Run(1, SyncStatus.Failed, "http_503"), Run(10, SyncStatus.Failed, "http_503"), Run(20, SyncStatus.Succeeded)]).Created);
        Assert.Single(Source([Run(1, SyncStatus.Failed, "token_invalid"), Run(20, SyncStatus.Succeeded)]).Created);
    }

    [Fact]
    public void SuccessfulSync_ClosesTheTechnicalAlarm()
    {
        var open = Source([Run(1, SyncStatus.Failed, "token_invalid")]).Created;

        var result = Source([Run(0, SyncStatus.Succeeded), Run(1, SyncStatus.Failed, "token_invalid")], existing: open);

        Assert.Equal(AlertState.Closed, Assert.Single(result.Updated).State);
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(29, 1)]
    [InlineData(13, 1)]
    [InlineData(6, 1)]
    public void TokenExpiry_WarnsAt30_14And7Days(int daysLeft, int expectedEvents)
    {
        var result = Source([Run(1, SyncStatus.Succeeded)], TestData.Noon(D).AddDays(daysLeft));

        Assert.Equal(expectedEvents, result.Created.Count);
        Assert.All(result.Created, e => Assert.Equal(RuleType.TokenExpiry, e.RuleType));
    }

    [Fact]
    public void TokenExpiry_EachStepIsRaisedOnce_AndRenewalClosesIt()
    {
        var expires = TestData.Noon(D).AddDays(13);
        var first = Source([Run(1, SyncStatus.Succeeded)], expires);

        Assert.Empty(Source([Run(1, SyncStatus.Succeeded)], expires, first.Created).Created);

        var renewed = Source([Run(1, SyncStatus.Succeeded)], TestData.Noon(D).AddDays(365), first.Created);
        Assert.Empty(renewed.Created);
        Assert.Equal(AlertState.Closed, Assert.Single(renewed.Updated).State);
    }

    [Fact]
    public void ExpiredToken_RaisesTechnicalAlarm()
    {
        var e = Assert.Single(Source([Run(1, SyncStatus.Succeeded)], TestData.Noon(D).AddDays(-1)).Created);

        Assert.Equal(RuleType.SourceFailure, e.RuleType);
        Assert.Contains("udløbet", e.Explanation);
    }
}
