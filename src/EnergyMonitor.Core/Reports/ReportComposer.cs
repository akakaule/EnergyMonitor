using System.Globalization;
using System.Text;
using EnergyMonitor.Core.Alerts;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Text;
using EnergyMonitor.Core.Time;

namespace EnergyMonitor.Core.Reports;

public static class ReportComposer
{
    public static string StatusLabel(PropertyStatus status) => status switch
    {
        PropertyStatus.Observer => "Observer",
        PropertyStatus.Alarm => "Alarm",
        PropertyStatus.DataMissing => "Data mangler",
        _ => "Normal",
    };

    /// <summary>
    /// The daily status text. It reports the latest <em>complete</em> day for each property and names the
    /// days that are not complete yet, so yesterday is never presented as final when it is not.
    /// A property with a linked heat pump gets one more line with its latest reading.
    /// </summary>
    public static string DailyReport(
        DateOnly today,
        IReadOnlyList<PropertySummary> summaries,
        IReadOnlyList<AlertEvent> systemEvents,
        DateTimeOffset? lastSuccessfulSyncLocal,
        IReadOnlyDictionary<string, HeatPumpReading>? heatPumps = null)
    {
        var sb = new StringBuilder();
        sb.Append("Energy Monitor · ").AppendLine(Da.LongDate(today));

        foreach (var s in summaries)
        {
            sb.Append(s.Property.Name).Append(": ");
            if (s.LatestComplete is null)
            {
                sb.AppendLine("Ingen komplette døgn endnu. Data mangler.");
                continue;
            }

            sb.Append("Seneste komplette døgn ").Append(Da.ShortDate(s.LatestComplete.LocalDate))
                .Append(": ").Append(Da.KWh(s.LatestComplete.KWh!.Value)).Append(". ");

            var low = s.OpenEvents.Any(e => e.RuleType == RuleType.LowConsumption);
            var high = s.OpenEvents.Any(e => e.RuleType == RuleType.HighConsumption);
            if (s.Baseline is { } baseline)
            {
                sb.Append(low ? "Lavere end normalt" : high ? "Højere end normalt" : "Median")
                    .Append(low || high ? $" (median {Da.KWh(baseline)}). " : $" {Da.KWh(baseline)}. ");
            }
            else
            {
                sb.Append("Utilstrækkelig historik til sammenligning. ");
            }

            if (s.Sum7 is { } sum7)
            {
                sb.Append("Seneste 7 døgn: ").Append(Da.KWh(sum7)).Append(". ");
            }

            sb.Append(StatusLabel(s.Status)).AppendLine(".");
        }

        foreach (var s in summaries)
        {
            if (heatPumps?.GetValueOrDefault(s.Property.Id) is { } reading)
            {
                sb.Append(s.Property.Name).Append(" · varmepumpe: ").AppendLine(HeatPumpLine(reading, LocalDay.Zone(s.Property.TimeZone)));
            }
        }

        // Properties waiting for the same days share one line.
        foreach (var group in summaries.Where(s => s.PendingDays.Count > 0)
            .GroupBy(s => (First: s.PendingDays[0], Last: s.PendingDays[^1])))
        {
            var days = group.Key.First == group.Key.Last
                ? Da.ShortDate(group.Key.First)
                : $"{Da.ShortDate(group.Key.First)}–{Da.ShortDate(group.Key.Last)}";
            sb.Append("Bemærk: Data for ").Append(days).Append(" er endnu ikke komplette (")
                .AppendJoin(", ", group.Select(s => s.Property.Name)).AppendLine(").");
        }

        foreach (var e in systemEvents.Where(e => e.State == AlertState.Open))
        {
            sb.Append("Teknisk: ").AppendLine(e.Explanation);
        }

        sb.Append("Seneste vellykkede datakontakt: ")
            .Append(lastSuccessfulSyncLocal is { } t ? $"{Da.ShortDate(DateOnly.FromDateTime(t.DateTime))} kl. {Da.Time(t)}" : "ingen endnu")
            .Append('.');
        // One line-ending style whatever the host OS: the text goes to a chat client.
        return sb.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>"9,5 °C inde, 60 % fugt (målt 14/11 kl. 08:55). Tændt: varme 10 °C." or "offline (sidst set 14/11 kl. 09:59)."</summary>
    public static string HeatPumpLine(HeatPumpReading r, TimeZoneInfo zone)
    {
        if (!r.Online)
        {
            return r.LastSeenUtc is { } seen ? $"offline (sidst set {HeatPumpRules.LastSeen(seen, zone)})." : "offline.";
        }

        var text = r.TemperatureC is { } t ? $"{Da.Celsius(t)} inde" : "ingen måling";
        if (r.Humidity is { } h)
        {
            text += $", {Da.Percent(Math.Round(h))} fugt";
        }

        // Always say when: after a Sensibo outage the last reading may be days old.
        if (r.MeasuredAtUtc is { } measured)
        {
            text += $" (målt {HeatPumpRules.LastSeen(measured, zone)})";
        }

        return text + r.On switch
        {
            true => $". Tændt: {Da.HeatPumpMode(r.Mode)}"
                + (r.TargetTemperature is { } target ? $" {target.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',')} °{r.TemperatureUnit ?? "C"}." : "."),
            false => ". Slukket.",
            null => ".",
        };
    }

    public static string AlertMessage(string propertyName, AlertEvent e)
    {
        var label = e.RuleType switch
        {
            RuleType.MissingData => "Data mangler",
            RuleType.SourceFailure or RuleType.TokenExpiry or RuleType.SensiboFailure => "Teknisk alarm",
            _ => Da.Severity(e.Severity),
        };
        return $"Energy Monitor · {propertyName}\n{label}: {e.Explanation}";
    }
}
