using EnergyMonitor.Core.Alerts;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Reports;

namespace EnergyMonitor.Tests.Core;

public class ReportTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);
    private static readonly DateTimeOffset Now = TestData.Noon(Today).AddHours(-4); // 09:00 local

    private static PropertySummary Summary(Property property, List<DailyConsumption> days, IReadOnlyList<AlertEvent>? events = null) =>
        PropertySummaryCalculator.Calculate(property, days, events ?? [], new RuleParams(), Now, TestData.Zone);

    [Fact]
    public void Summary_ReportsLatestCompleteDay_AndListsNewerDaysAsPending()
    {
        var days = TestData.History(Today.AddDays(-1), 20, 8); // complete through 6/11
        days.Add(TestData.Day(Today.AddDays(-1), 3, DayQuality.Partial));

        var s = Summary(TestData.Sommerhus(), days);

        Assert.Equal(new DateOnly(2026, 11, 6), s.LatestComplete!.LocalDate);
        Assert.Equal([new DateOnly(2026, 11, 7)], s.PendingDays);
        Assert.Equal(PropertyStatus.Normal, s.Status);
        Assert.Equal(8, s.Baseline);
        Assert.Equal(0, s.DeviationKWh);
        Assert.Equal(56, s.Sum7);
        Assert.Null(s.Sum30); // only 20 complete days
        Assert.False(s.IsStale);
        Assert.Equal(33, s.DataAgeHours);
    }

    [Fact]
    public void Sum7_IsNullWhenTheWindowHasAHole()
    {
        var days = TestData.History(Today.AddDays(-1), 20, 8);
        days[^3] = days[^3] with { Quality = DayQuality.Missing, KWh = null };

        Assert.Null(Summary(TestData.Sommerhus(), days).Sum7);
    }

    [Fact]
    public void Summary_NoData_IsDataMissing_NeverZero()
    {
        var s = Summary(TestData.Sommerhus(), []);

        Assert.Equal(PropertyStatus.DataMissing, s.Status);
        Assert.Null(s.LatestComplete);
        Assert.Null(s.Sum7);
        Assert.True(s.IsStale);
    }

    [Fact]
    public void Summary_StatusFollowsOpenEvents()
    {
        var days = TestData.History(Today.AddDays(-1), 14, 8);
        days.Add(TestData.Day(Today.AddDays(-1), 1.2));
        var observer = RuleEngine.EvaluateProperty(TestData.Sommerhus(), TestData.DefaultRules(), days, [], Today.AddDays(-7), Now, TestData.Zone).Created;

        Assert.Equal(PropertyStatus.Observer, Summary(TestData.Sommerhus(), days, observer).Status);
        Assert.Equal(PropertyStatus.Alarm, Summary(TestData.Sommerhus(), days, [observer[0] with { Severity = AlertSeverity.Alarm }]).Status);
        Assert.Equal(PropertyStatus.Normal, Summary(TestData.Sommerhus(), days, [observer[0] with { State = AlertState.Closed }]).Status);
        Assert.Equal(PropertyStatus.DataMissing, Summary(TestData.Sommerhus(), days, [observer[0] with { RuleType = RuleType.MissingData }]).Status);
    }

    [Fact]
    public void DailyReport_MatchesTheSpecExample()
    {
        var sommerhusDays = TestData.History(new DateOnly(2026, 11, 6), 14, 7.4);
        sommerhusDays.Add(TestData.Day(new DateOnly(2026, 11, 6), 1.2));
        var events = RuleEngine.EvaluateProperty(TestData.Sommerhus(), TestData.DefaultRules(), sommerhusDays, [], Today.AddDays(-7), Now, TestData.Zone).Created;
        var hjemDays = TestData.History(new DateOnly(2026, 11, 6), 14, 15, "hjem");
        hjemDays.Add(TestData.Day(new DateOnly(2026, 11, 6), 15.8, propertyId: "hjem"));

        var text = ReportComposer.DailyReport(
            Today,
            [Summary(TestData.Sommerhus(), sommerhusDays, events), Summary(TestData.Hjem(), hjemDays)],
            [],
            new DateTimeOffset(2026, 11, 8, 8, 0, 0, TimeSpan.FromHours(1)));

        Assert.StartsWith("Energy Monitor · 8. november", text);
        Assert.Contains("Sommerhus: Seneste komplette døgn 6/11: 1,2 kWh. Lavere end normalt (median 7,4 kWh).", text);
        Assert.Contains("Observer.", text);
        Assert.Contains("Hjem: Seneste komplette døgn 6/11: 15,8 kWh.", text);
        Assert.Contains("Normal.", text);
        Assert.Contains("Bemærk: Data for 7/11 er endnu ikke komplette", text);
        Assert.Contains("Seneste vellykkede datakontakt: 8/11 kl. 08:00.", text);
        Assert.DoesNotContain("7/11:", text); // yesterday is never presented as a complete day
    }

    [Fact]
    public void DailyReport_PropertyWithoutData_SaysDataMangler()
    {
        var text = ReportComposer.DailyReport(Today, [Summary(TestData.Sommerhus(), [])], [], null);

        Assert.Contains("Sommerhus: Ingen komplette døgn endnu. Data mangler.", text);
        Assert.Contains("ingen endnu", text);
        Assert.DoesNotContain("0,0 kWh", text);
    }
}
