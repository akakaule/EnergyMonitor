using EnergyMonitor.Core.Alerts;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Reports;

namespace EnergyMonitor.Tests.Core;

public class HeatPumpRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 14);

    private static List<AlertRule> Rules(bool enabled = true) =>
        [.. AlertRule.HeatPumpRuleTypes.Select(t => AlertRule.Default("sommerhus", t, TestData.Created) with { Enabled = enabled })];

    private static HeatPumpReading Reading(double? temperature, DateTimeOffset at, bool online = true, DateTimeOffset? lastSeen = null) =>
        new("pod1", "Stue", temperature, 60, at, online, lastSeen ?? at);

    private static RuleEvaluation Evaluate(HeatPumpReading reading, IReadOnlyList<AlertEvent> existing, DateTimeOffset now, bool enabled = true) =>
        HeatPumpRules.EvaluateProperty(TestData.Sommerhus(), Rules(enabled), reading, existing, now, TestData.Zone);

    /// <summary>Applies an evaluation to the stored events, as the alert service does.</summary>
    private static List<AlertEvent> Apply(List<AlertEvent> events, RuleEvaluation result)
    {
        var byId = events.ToDictionary(e => e.Id);
        foreach (var e in result.Created.Concat(result.Updated))
        {
            byId[e.Id] = e;
        }

        return [.. byId.Values];
    }

    [Fact]
    public void Cold_OpensAnObserver_ThenEscalatesToAlarm_AfterTheSustainedMinutes()
    {
        var first = Evaluate(Reading(3.5, Now), [], Now);
        var e = Assert.Single(first.Created);
        Assert.Equal((RuleType.IndoorTooCold, AlertSeverity.Observer, AlertState.Open, Today), (e.RuleType, e.Severity, e.State, e.FirstLocalDate));
        Assert.Equal((3.5, 5.0), (e.Observed, e.Threshold));
        Assert.Contains("3,5 °C", e.Explanation, StringComparison.Ordinal);
        Assert.Equal("sommerhus~IndoorTooCold~2026-11-14", e.Id);

        var events = Apply([], first);
        var stillCold = Evaluate(Reading(3.2, Now.AddMinutes(90)), events, Now.AddMinutes(90));
        Assert.Empty(stillCold.Created);
        Assert.Equal(AlertSeverity.Observer, Assert.Single(stillCold.Updated).Severity);

        events = Apply(events, stillCold);
        var escalated = Assert.Single(Evaluate(Reading(3.0, Now.AddMinutes(120)), events, Now.AddMinutes(120)).Updated);
        Assert.Equal((AlertSeverity.Alarm, 3.0), (escalated.Severity, escalated.Observed));
        Assert.True(RuleEngine.NeedsNotification(escalated with { LastNotifiedSeverity = AlertSeverity.Observer }));
    }

    [Fact]
    public void Cold_ClosesOnlyOnceTheTemperatureIsAThresholdPlusHysteresis()
    {
        var events = Apply([], Evaluate(Reading(4.0, Now), [], Now));

        var nearlyWarm = Evaluate(Reading(5.6, Now.AddMinutes(15)), events, Now.AddMinutes(15));
        Assert.All(nearlyWarm.Updated, e => Assert.Equal(AlertState.Open, e.State));
        events = Apply(events, nearlyWarm);

        var warm = Assert.Single(Evaluate(Reading(6.0, Now.AddMinutes(30)), events, Now.AddMinutes(30)).Updated);
        Assert.Equal(AlertState.Closed, warm.State);
        Assert.Equal(Now.AddMinutes(30), warm.ClosedAtUtc);
    }

    [Fact]
    public void ColdAgainTheSameDay_AfterClosing_RaisesASecondEvent()
    {
        var events = Apply([], Evaluate(Reading(4.0, Now), [], Now));
        events = Apply(events, Evaluate(Reading(8.0, Now.AddHours(1)), events, Now.AddHours(1)));

        var again = Assert.Single(Evaluate(Reading(4.5, Now.AddHours(3)), events, Now.AddHours(3)).Created);

        Assert.Equal(1, again.Sequence);
        Assert.Equal("sommerhus~IndoorTooCold~2026-11-14~1", again.Id);
        Assert.True(RuleEngine.NeedsNotification(again));
    }

    [Fact]
    public void RepeatedEvaluation_OfTheSameReading_ChangesNothingNew()
    {
        var reading = Reading(3.5, Now);
        var events = Apply([], Evaluate(reading, [], Now));

        var again = Evaluate(reading, events, Now);

        Assert.Empty(again.Created);
        Assert.All(again.Updated, e => Assert.Equal(AlertSeverity.Observer, e.Severity));
    }

    [Fact]
    public void Offline_PastTheLimit_RaisesAnAlarm_AndClosesWhenTheDeviceIsBack()
    {
        var lastSeen = Now.AddMinutes(-61);
        var result = Evaluate(Reading(9, lastSeen, online: false, lastSeen), [], Now);

        var e = Assert.Single(result.Created);
        Assert.Equal((RuleType.HeatPumpOffline, AlertSeverity.Alarm), (e.RuleType, e.Severity));
        Assert.Contains("sidst set 14/11 kl. 09:59", e.Explanation, StringComparison.Ordinal);

        var back = Assert.Single(Evaluate(Reading(9, Now.AddMinutes(15)), Apply([], result), Now.AddMinutes(15)).Updated);
        Assert.Equal(AlertState.Closed, back.State);
    }

    [Fact]
    public void Offline_ForAShortWhile_RaisesNothing_AndAStaleTemperatureIsNotJudged()
    {
        var lastSeen = Now.AddMinutes(-30);

        var result = Evaluate(Reading(2, lastSeen, online: false, lastSeen), [], Now);

        Assert.Empty(result.Created);
    }

    [Fact]
    public void DisablingTheRule_OrUnlinkingThePump_ClosesItsOpenEvents()
    {
        var lastSeen = Now.AddHours(-5);
        var cold = Assert.Single(Evaluate(Reading(1, Now), [], Now).Created);
        var offline = Assert.Single(Evaluate(Reading(1, lastSeen, online: false, lastSeen), [], Now).Created);

        var disabled = Evaluate(Reading(1, Now.AddMinutes(15)), [cold, offline], Now.AddMinutes(15), enabled: false);
        var unlinked = HeatPumpRules.EvaluateProperty(TestData.Sommerhus(), [], null, [cold, offline], Now.AddMinutes(15), TestData.Zone);

        Assert.Equal(2, disabled.Updated.Count(e => e.State == AlertState.Closed));
        Assert.Equal(2, unlinked.Updated.Count(e => e.State == AlertState.Closed));
        Assert.Empty(unlinked.Created);
    }

    [Fact]
    public void DisabledRules_RaiseNothing()
    {
        var lastSeen = Now.AddHours(-5);

        Assert.Empty(Evaluate(Reading(1, Now), [], Now, enabled: false).Created);
        Assert.Empty(Evaluate(Reading(1, lastSeen, online: false, lastSeen), [], Now, enabled: false).Created);
    }

    [Fact]
    public void SensiboFailures_RaiseOneTechnicalAlarm_AndASuccessClosesIt()
    {
        var p = RuleParams.DefaultFor(RuleType.SensiboFailure);

        Assert.Empty(HeatPumpRules.EvaluateSource(2, "sensibo_unreachable", [], p, Now, TestData.Zone).Created);

        var raised = Assert.Single(HeatPumpRules.EvaluateSource(3, "sensibo_unreachable", [], p, Now, TestData.Zone).Created);
        Assert.Equal((AlertEvent.SystemPropertyId, RuleType.SensiboFailure, AlertSeverity.Alarm), (raised.PropertyId, raised.RuleType, raised.Severity));

        Assert.Empty(HeatPumpRules.EvaluateSource(4, "sensibo_unreachable", [raised], p, Now.AddMinutes(15), TestData.Zone).Created);
        var closed = Assert.Single(HeatPumpRules.EvaluateSource(0, null, [raised], p, Now.AddMinutes(30), TestData.Zone).Updated);
        Assert.Equal(AlertState.Closed, closed.State);
    }

    [Fact]
    public void ARejectedSensiboKey_RaisesTheTechnicalAlarmAtOnce()
    {
        var p = RuleParams.DefaultFor(RuleType.SensiboFailure);

        var raised = Assert.Single(HeatPumpRules.EvaluateSource(1, "sensibo_key_rejected", [], p, Now, TestData.Zone).Created);

        Assert.Contains("API-nøgle", raised.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void EventIds_WithoutASequence_AreUnchanged_AndSequencedIdsRoundTrip()
    {
        Assert.Equal("hjem~LowConsumption~2026-11-06", AlertEvent.MakeId("hjem", RuleType.LowConsumption, new DateOnly(2026, 11, 6)));

        Assert.True(AlertEvent.TryParseId("sommerhus~IndoorTooCold~2026-11-14~2", out var property, out var type, out var date, out var sequence));
        Assert.Equal(("sommerhus", RuleType.IndoorTooCold, Today, 2), (property, type, date, sequence));
        Assert.True(AlertEvent.TryParseId("hjem~LowConsumption~2026-11-06", out _, out _, out _, out var none));
        Assert.Equal(0, none);
        Assert.False(AlertEvent.TryParseId("sommerhus~IndoorTooCold~2026-11-14~x", out _, out _, out _, out _));
    }

    [Fact]
    public void AlertMessage_AndDailyReport_DescribeTheHeatPump()
    {
        var cold = Assert.Single(Evaluate(Reading(3.5, Now), [], Now).Created);
        Assert.StartsWith("Energy Monitor · Sommerhus\nObserver: Indetemperaturen", ReportComposer.AlertMessage("Sommerhus", cold), StringComparison.Ordinal);

        var summary = PropertySummaryCalculator.Calculate(
            TestData.Sommerhus(), TestData.History(Today, 20), [], RuleParams.DefaultFor(RuleType.LowConsumption), Now, TestData.Zone);
        var heatPumps = new Dictionary<string, HeatPumpReading>
        {
            ["sommerhus"] = Reading(9.5, Now) with { On = true, Mode = "heat", TargetTemperature = 10, TemperatureUnit = "C" },
        };

        var report = ReportComposer.DailyReport(Today, [summary], [], null, heatPumps);

        // When it was measured, so a reading from before a Sensibo outage is never taken for today's.
        Assert.Contains("Sommerhus · varmepumpe: 9,5 °C inde, 60 % fugt (målt 14/11 kl. 11:00). Tændt: varme 10 °C.", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeatPumpAlarm_OutranksMissingData_OnTheDashboard()
    {
        var missing = new AlertEvent("sommerhus", "MissingData", RuleType.MissingData, Today, Today, AlertSeverity.Observer, AlertState.Open,
            null, null, 72, "", new RuleParams(), Now);
        var cold = Assert.Single(Evaluate(Reading(3.5, Now), [], Now).Created) with { Severity = AlertSeverity.Alarm };

        var summary = PropertySummaryCalculator.Calculate(
            TestData.Sommerhus(), TestData.History(Today, 20), [missing, cold], RuleParams.DefaultFor(RuleType.LowConsumption), Now, TestData.Zone);

        Assert.Equal(PropertyStatus.Alarm, summary.Status);
    }
}
