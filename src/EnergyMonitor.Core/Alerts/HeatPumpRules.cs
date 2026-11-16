using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Text;
using EnergyMonitor.Core.Time;

namespace EnergyMonitor.Core.Alerts;

/// <summary>
/// Rules for a property's heat pump, evaluated on every collection (every 15 minutes) rather than per day.
/// Pure, like <see cref="RuleEngine"/>: the same reading and events always give the same result.
/// </summary>
public static class HeatPumpRules
{
    /// <summary>A cold event closes only this far above the limit, so a reading that wobbles around it does not flap.</summary>
    public const double HysteresisC = 1;

    /// <summary>The collector's error code for an API key Sensibo refuses.</summary>
    public const string KeyRejected = "sensibo_key_rejected";

    /// <summary>
    /// Evaluates a property's heat pump rules on its latest <paramref name="reading"/>. Without a reading (the pump
    /// was unlinked or is gone) or with a rule disabled, that rule's open event is closed, so the house never stays
    /// in alarm about something nobody watches any more.
    /// </summary>
    public static RuleEvaluation EvaluateProperty(
        Property property,
        IReadOnlyList<AlertRule> rules,
        HeatPumpReading? reading,
        IReadOnlyList<AlertEvent> existing,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        var work = new RuleEngine.WorkingSet(existing);
        var today = LocalDay.Of(now, zone);

        foreach (var type in AlertRule.HeatPumpRuleTypes)
        {
            var watched = reading is not null && rules.Any(r => r.Type == type && r.Enabled);
            if (!watched && Open(work, property.Id, type) is { } orphan)
            {
                work.Update(orphan with { State = AlertState.Closed, ClosedAtUtc = now });
            }
        }

        if (reading is null)
        {
            return work.ToResult();
        }

        if (rules.FirstOrDefault(r => r.Type == RuleType.IndoorTooCold) is { Enabled: true } cold
            && reading.Online && reading.TemperatureC is { } temperature)
        {
            EvaluateCold(property, cold, temperature, reading.MeasuredAtUtc ?? now, today, now, zone, work);
        }

        if (rules.FirstOrDefault(r => r.Type == RuleType.HeatPumpOffline) is { Enabled: true } offline)
        {
            EvaluateOffline(property, offline, reading, today, now, zone, work);
        }

        return work.ToResult();
    }

    private static void EvaluateCold(
        Property property, AlertRule rule, double temperature, DateTimeOffset measuredAt, DateOnly today, DateTimeOffset now, TimeZoneInfo zone, RuleEngine.WorkingSet work)
    {
        var p = rule.Params;
        var threshold = p.MinTemperatureC;
        var open = Open(work, property.Id, RuleType.IndoorTooCold);

        if (temperature < threshold)
        {
            if (open is null)
            {
                var severity = p.SustainedMinutes <= 0 ? AlertSeverity.Alarm : AlertSeverity.Observer;
                work.Create(new AlertEvent(
                    property.Id, rule.Id, rule.Type, today, today, severity, AlertState.Open, temperature, null, threshold,
                    ExplainCold(temperature, threshold, measuredAt, zone, null), p, now, Sequence: NextSequence(work, property.Id, rule.Type, today)));
                return;
            }

            var minutes = (now - open.CreatedAtUtc).TotalMinutes;
            var escalated = minutes >= p.SustainedMinutes ? AlertSeverity.Alarm : open.Severity;
            work.Update(open with
            {
                LastLocalDate = today,
                Severity = escalated,
                Observed = temperature,
                Threshold = threshold,
                ParamsSnapshot = p,
                Explanation = ExplainCold(temperature, threshold, measuredAt, zone, escalated == AlertSeverity.Alarm ? minutes : null),
            });
            return;
        }

        if (open is null)
        {
            return;
        }

        work.Update(temperature >= threshold + HysteresisC
            ? open with { State = AlertState.Closed, ClosedAtUtc = now, Observed = temperature }
            : open with { LastLocalDate = today, Observed = temperature });
    }

    private static string ExplainCold(double temperature, double threshold, DateTimeOffset measuredAt, TimeZoneInfo zone, double? minutes)
    {
        var text = $"Indetemperaturen er {Da.Celsius(temperature)} kl. {Da.Time(TimeZoneInfo.ConvertTime(measuredAt, zone))}, under grænsen på {Da.Celsius(threshold)}.";
        if (minutes is { } m)
        {
            text += $" Det har den været i over {Duration(m)}.";
        }

        return text + " Tjek varmepumpen og strømmen i huset.";
    }

    private static void EvaluateOffline(
        Property property, AlertRule rule, HeatPumpReading reading, DateOnly today, DateTimeOffset now, TimeZoneInfo zone, RuleEngine.WorkingSet work)
    {
        var p = rule.Params;
        var seen = reading.LastSeenUtc ?? reading.MeasuredAtUtc;
        var open = Open(work, property.Id, RuleType.HeatPumpOffline);

        if (reading.Online)
        {
            if (open is not null)
            {
                work.Update(open with { State = AlertState.Closed, ClosedAtUtc = now });
            }

            return;
        }

        var minutes = seen is { } s ? (now - s).TotalMinutes : (double?)null;
        if (open is not null || minutes <= p.OfflineMinutes)
        {
            return;
        }

        var explanation = seen is { } last
            ? $"Varmepumpen har ikke haft forbindelse til Sensibo i over {Duration(minutes!.Value)} (sidst set {LastSeen(last, zone)}). Det kan skyldes strømsvigt eller manglende internet i huset."
            : "Varmepumpen har ikke forbindelse til Sensibo. Det kan skyldes strømsvigt eller manglende internet i huset.";
        work.Create(new AlertEvent(
            property.Id, rule.Id, rule.Type, today, today, AlertSeverity.Alarm, AlertState.Open,
            minutes is { } observed ? Math.Floor(observed) : null, null, p.OfflineMinutes, explanation, p, now,
            Sequence: NextSequence(work, property.Id, rule.Type, today)));
    }

    /// <summary>
    /// Technical alarm about Sensibo itself: a rejected API key at once, other failures after
    /// <see cref="RuleParams.ConsecutiveFailures"/> collections in a row. The first success closes it.
    /// </summary>
    public static RuleEvaluation EvaluateSource(
        int consecutiveFailures,
        string? lastError,
        IReadOnlyList<AlertEvent> existing,
        RuleParams p,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        const string system = AlertEvent.SystemPropertyId;
        var work = new RuleEngine.WorkingSet(existing);
        var open = Open(work, system, RuleType.SensiboFailure);

        if (consecutiveFailures == 0)
        {
            if (open is not null)
            {
                work.Update(open with { State = AlertState.Closed, ClosedAtUtc = now });
            }

            return work.ToResult();
        }

        var rejected = lastError == KeyRejected;
        if (open is not null || (!rejected && consecutiveFailures < p.ConsecutiveFailures))
        {
            return work.ToResult();
        }

        var today = LocalDay.Of(now, zone);
        var explanation = rejected
            ? "Sensibo afviste API-nøglen, så varmepumpen overvåges ikke. Opret en ny nøgle på home.sensibo.com og opdater Sensibo--ApiKey i Key Vault."
            : $"{consecutiveFailures} hentninger fra Sensibo i træk er fejlet (seneste fejl: {lastError ?? "ukendt"}). Varmepumpen overvåges ikke, så længe det står på.";
        work.Create(new AlertEvent(
            system, RuleType.SensiboFailure.ToString(), RuleType.SensiboFailure, today, today, AlertSeverity.Alarm, AlertState.Open,
            consecutiveFailures, null, p.ConsecutiveFailures, explanation, p, now, Sequence: NextSequence(work, system, RuleType.SensiboFailure, today)));
        return work.ToResult();
    }

    /// <summary>"sidst set 14/11 kl. 09:59" without the prefix, in the property's local time.</summary>
    public static string LastSeen(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        return $"{Da.ShortDate(DateOnly.FromDateTime(local.DateTime))} kl. {Da.Time(local)}";
    }

    private static AlertEvent? Open(RuleEngine.WorkingSet work, string propertyId, RuleType type) =>
        work.All.Where(e => e.PropertyId == propertyId && e.RuleType == type && e.State == AlertState.Open).MaxBy(e => e.CreatedAtUtc);

    private static int NextSequence(RuleEngine.WorkingSet work, string propertyId, RuleType type, DateOnly day)
    {
        var sameDay = work.All.Where(e => e.PropertyId == propertyId && e.RuleType == type && e.FirstLocalDate == day).ToList();
        return sameDay.Count == 0 ? 0 : sameDay.Max(e => e.Sequence) + 1;
    }

    private static string Duration(double minutes) =>
        minutes >= 120 ? $"{Math.Floor(minutes / 60):0} timer" : $"{Math.Floor(minutes):0} min";
}
