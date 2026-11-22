using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Text;
using EnergyMonitor.Core.Time;
using EnergyMonitor.Core.Weather;

namespace EnergyMonitor.Core.Alerts;

public sealed record RuleEvaluation(IReadOnlyList<AlertEvent> Created, IReadOnlyList<AlertEvent> Updated)
{
    public static readonly RuleEvaluation None = new([], []);
}

/// <summary>
/// Deterministic v1 rules. Pure: the same days, rules and existing events always give the same result,
/// so re-running after a repeated sync or a restart creates nothing new.
/// <para>
/// Low and high consumption compare a day with the median of the preceding days, or, on the days an
/// expectation from the outdoor temperature is given and the rule allows it, with that expectation: a cold
/// snap is then not "high", and a mild week does not hide a house that has stopped heating.
/// </para>
/// </summary>
public static class RuleEngine
{
    public static readonly int[] TokenWarningDays = [7, 14, 30];

    public static RuleEvaluation EvaluateProperty(
        Property property,
        IReadOnlyList<AlertRule> rules,
        IReadOnlyList<DailyConsumption> days,
        IReadOnlyList<AlertEvent> existing,
        DateOnly evaluateFrom,
        DateTimeOffset now,
        TimeZoneInfo zone,
        IReadOnlyDictionary<DateOnly, WeatherExpectation>? expected = null)
    {
        var work = new WorkingSet(existing);
        var ordered = days.OrderBy(d => d.LocalDate).ToList();

        foreach (var type in new[] { RuleType.LowConsumption, RuleType.HighConsumption })
        {
            var rule = rules.FirstOrDefault(r => r.Type == type);
            if (rule is { Enabled: true } && property.MonitoringEnabled)
            {
                EvaluateRelative(property, rule, ordered, evaluateFrom, now, work, expected);
            }
        }

        var missing = rules.FirstOrDefault(r => r.Type == RuleType.MissingData);
        if (missing is { Enabled: true })
        {
            EvaluateMissing(property, missing, ordered, now, zone, work);
        }

        return work.ToResult();
    }

    private static void EvaluateRelative(
        Property property,
        AlertRule rule,
        List<DailyConsumption> days,
        DateOnly evaluateFrom,
        DateTimeOffset now,
        WorkingSet work,
        IReadOnlyDictionary<DateOnly, WeatherExpectation>? expected)
    {
        var low = rule.Type == RuleType.LowConsumption;
        var p = rule.Params;

        // Only complete local days are ever analysed; partial and missing days are skipped entirely.
        foreach (var day in days.Where(d => d.IsComplete && d.LocalDate >= evaluateFrom))
        {
            // A day with an expectation from its outdoor temperature is judged against that; the rest against the median.
            var weather = p.WeatherAdjusted ? expected?.GetValueOrDefault(day.LocalDate) : null;
            var baseline = weather is null ? Baseline.For(days, day.LocalDate, p) : Math.Round(Math.Max(0, weather.ExpectedKWh), 3);
            if (baseline is null)
            {
                continue;
            }

            var kWh = day.KWh!.Value;
            var threshold = Math.Round(baseline.Value * p.ThresholdPercent / 100, 3);
            var triggered = baseline.Value >= p.MinBaselineKWh && (low ? kWh < threshold : kWh > threshold);
            var ofType = work.All.Where(e => e.PropertyId == property.Id && e.RuleType == rule.Type).ToList();

            if (!triggered)
            {
                foreach (var open in ofType.Where(e => e.State == AlertState.Open && e.LastLocalDate < day.LocalDate))
                {
                    work.Update(open with { State = AlertState.Closed, ClosedAtUtc = now });
                }

                continue;
            }

            if (ofType.Any(e => e.FirstLocalDate <= day.LocalDate && day.LocalDate <= e.LastLocalDate))
            {
                // Already part of an event (open or closed): never raise the same day twice.
                continue;
            }

            var running = ofType
                .Where(e => e.State == AlertState.Open && e.LastLocalDate < day.LocalDate)
                .MaxBy(e => e.LastLocalDate);

            if (running is null)
            {
                var severity = low && p.EscalateAfterDays <= 1 ? AlertSeverity.Alarm : AlertSeverity.Observer;
                work.Create(new AlertEvent(
                    property.Id, rule.Id, rule.Type, day.LocalDate, day.LocalDate, severity, AlertState.Open,
                    kWh, baseline, threshold,
                    Explain(low, day.LocalDate, kWh, baseline.Value, p, 1, weather),
                    p, now) { OutdoorC = weather?.OutdoorC });
                continue;
            }

            // Open events are closed by the first normal complete day, so every complete day between the
            // event's first day and this one was also outside the threshold.
            var consecutive = days.Count(d => d.IsComplete && d.LocalDate >= running.FirstLocalDate && d.LocalDate <= day.LocalDate);
            var escalated = low && consecutive >= p.EscalateAfterDays ? AlertSeverity.Alarm : running.Severity;
            work.Update(running with
            {
                LastLocalDate = day.LocalDate,
                Severity = escalated,
                Observed = kWh,
                Baseline = baseline,
                Threshold = threshold,
                ParamsSnapshot = p,
                Explanation = Explain(low, day.LocalDate, kWh, baseline.Value, p, consecutive, weather),
                OutdoorC = weather?.OutdoorC,
            });
        }
    }

    private static string Explain(bool low, DateOnly date, double kWh, double baseline, RuleParams p, int consecutive, WeatherExpectation? weather)
    {
        var text = $"Døgnforbrug {Da.KWh(kWh)} d. {Da.ShortDate(date)} er {(low ? "under" : "over")} {Da.Percent(p.ThresholdPercent)} af "
            + (weather is null
                ? $"medianen ({Da.KWh(baseline)}) for de foregående komplette døgn."
                : $"det forventede ({Da.KWh(baseline)}) ved {Da.Celsius(weather.OutdoorC)} ude.");
        if (consecutive > 1)
        {
            text += $" {consecutive} døgn i træk.";
        }

        return low ? text + " Lavt forbrug er kun en indikator på mulig fejl." : text;
    }

    private static void EvaluateMissing(
        Property property,
        AlertRule rule,
        List<DailyConsumption> days,
        DateTimeOffset now,
        TimeZoneInfo zone,
        WorkingSet work)
    {
        var latest = days.LastOrDefault(d => d.IsComplete);
        var reference = latest is null ? property.CreatedAt : LocalDay.Bounds(latest.LocalDate, zone).EndUtc;
        var firstMissing = latest?.LocalDate.AddDays(1) ?? LocalDay.Of(property.CreatedAt, zone);
        var hours = (now - reference).TotalHours;

        // A newer complete day ends any earlier gap.
        foreach (var open in work.All.Where(e =>
            e.PropertyId == property.Id && e.RuleType == RuleType.MissingData && e.State == AlertState.Open
            && e.FirstLocalDate < firstMissing).ToList())
        {
            work.Update(open with { State = AlertState.Closed, ClosedAtUtc = now });
        }

        if (hours <= rule.Params.MissingHours)
        {
            return;
        }

        if (work.All.Any(e => e.PropertyId == property.Id && e.RuleType == RuleType.MissingData && e.FirstLocalDate == firstMissing))
        {
            return;
        }

        var explanation = latest is null
            ? $"Der er ikke modtaget et komplet døgn i over {rule.Params.MissingHours} timer. Det kan skyldes forsinkelse hos dataudbyderen."
            : $"Intet nyt komplet døgn siden {Da.ShortDate(latest.LocalDate)} (over {rule.Params.MissingHours} timer). Det kan skyldes forsinkelse hos dataudbyderen og er ikke en forbrugsalarm.";
        work.Create(new AlertEvent(
            property.Id, rule.Id, RuleType.MissingData, firstMissing, firstMissing, AlertSeverity.Observer, AlertState.Open,
            null, null, rule.Params.MissingHours, explanation, rule.Params, now));
    }

    /// <summary>
    /// Technical alarms about the data source: repeated failed sync runs, a rejected or expired token,
    /// and warnings 30, 14 and 7 days before a known token expiry.
    /// </summary>
    public static RuleEvaluation EvaluateSource(
        IReadOnlyList<SyncRun> recentRunsNewestFirst,
        DateTimeOffset? tokenExpiresAt,
        IReadOnlyList<AlertEvent> existing,
        RuleParams p,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        var work = new WorkingSet(existing);
        const string system = AlertEvent.SystemPropertyId;

        var finished = recentRunsNewestFirst.Where(r => r.Status != SyncStatus.Running).ToList();
        var streak = finished.TakeWhile(r => r.Status == SyncStatus.Failed).ToList();
        var tokenRejected = streak.Count > 0 && streak[0].ErrorCode == "token_invalid";
        var tokenExpired = tokenExpiresAt is { } exp && exp <= now;
        var failing = streak.Count >= p.ConsecutiveFailures || tokenRejected || tokenExpired;
        var openFailures = work.All.Where(e => e.PropertyId == system && e.RuleType == RuleType.SourceFailure && e.State == AlertState.Open).ToList();

        if (!failing)
        {
            if (streak.Count == 0)
            {
                foreach (var open in openFailures)
                {
                    work.Update(open with { State = AlertState.Closed, ClosedAtUtc = now });
                }
            }
        }
        else if (openFailures.Count == 0)
        {
            var first = streak.Count > 0 ? LocalDay.Of(streak[^1].StartedAtUtc, zone) : LocalDay.Of(now, zone);
            var id = AlertEvent.MakeId(system, RuleType.SourceFailure, first);
            if (work.All.All(e => e.Id != id))
            {
                var explanation = tokenExpired ? "Eloverblik-adgangsnøglen er udløbet. Opret en ny nøgle i Eloverblik og opdater den i Key Vault."
                    : tokenRejected ? "Eloverblik afviste adgangsnøglen. Kontroller eller forny nøglen."
                    : $"{streak.Count} synkroniseringer i træk er fejlet (seneste fejl: {streak[0].ErrorCode ?? "ukendt"}).";
                work.Create(new AlertEvent(
                    system, RuleType.SourceFailure.ToString(), RuleType.SourceFailure, first, first, AlertSeverity.Alarm,
                    AlertState.Open, streak.Count, null, p.ConsecutiveFailures, explanation, p, now));
            }
        }

        EvaluateTokenExpiry(tokenExpiresAt, now, zone, p, work);
        return work.ToResult();
    }

    private static void EvaluateTokenExpiry(DateTimeOffset? tokenExpiresAt, DateTimeOffset now, TimeZoneInfo zone, RuleParams p, WorkingSet work)
    {
        const string system = AlertEvent.SystemPropertyId;
        string? currentId = null;

        if (tokenExpiresAt is { } expires && expires > now)
        {
            var daysLeft = (expires - now).TotalDays;
            var crossed = TokenWarningDays.Where(t => daysLeft <= t).Select(t => (int?)t).FirstOrDefault();
            if (crossed is { } threshold)
            {
                var expiryDate = LocalDay.Of(expires, zone);
                var first = expiryDate.AddDays(-threshold);
                currentId = AlertEvent.MakeId(system, RuleType.TokenExpiry, first);
                if (work.All.All(e => e.Id != currentId))
                {
                    work.Create(new AlertEvent(
                        system, RuleType.TokenExpiry.ToString(), RuleType.TokenExpiry, first, expiryDate,
                        threshold <= 7 ? AlertSeverity.Alarm : AlertSeverity.Observer, AlertState.Open,
                        Math.Floor(daysLeft), null, threshold,
                        $"Eloverblik-adgangsnøglen udløber {Da.LongDate(expiryDate)} (om {Math.Floor(daysLeft):0} dage). Opret en ny nøgle i Eloverblik og opdater den i Key Vault.",
                        p, now));
                }
            }
        }

        // A renewed token, or a later warning step, ends the earlier warnings.
        foreach (var open in work.All.Where(e =>
            e.PropertyId == system && e.RuleType == RuleType.TokenExpiry && e.State == AlertState.Open && e.Id != currentId).ToList())
        {
            work.Update(open with { State = AlertState.Closed, ClosedAtUtc = now });
        }
    }

    /// <summary>
    /// True when a message should go out: once when the event is raised and again only if severity rises.
    /// </summary>
    public static bool NeedsNotification(AlertEvent e) =>
        e.State == AlertState.Open && (e.LastNotifiedSeverity is null || e.Severity > e.LastNotifiedSeverity);

    /// <summary>The events of one evaluation: the stored ones plus what this run creates or changes.</summary>
    internal sealed class WorkingSet(IReadOnlyList<AlertEvent> existing)
    {
        private readonly Dictionary<string, AlertEvent> _events = existing.ToDictionary(e => e.Id);
        private readonly HashSet<string> _created = [];
        private readonly HashSet<string> _updated = [];

        public IEnumerable<AlertEvent> All => _events.Values;

        public void Create(AlertEvent e)
        {
            if (_events.TryAdd(e.Id, e))
            {
                _created.Add(e.Id);
            }
        }

        public void Update(AlertEvent e)
        {
            _events[e.Id] = e;
            if (!_created.Contains(e.Id))
            {
                _updated.Add(e.Id);
            }
        }

        public RuleEvaluation ToResult() => new(
            [.. _created.Select(id => _events[id])],
            [.. _updated.Select(id => _events[id])]);
    }
}
