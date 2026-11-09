using System.Text;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Text;

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
    /// </summary>
    public static string DailyReport(
        DateOnly today,
        IReadOnlyList<PropertySummary> summaries,
        IReadOnlyList<AlertEvent> systemEvents,
        DateTimeOffset? lastSuccessfulSyncLocal)
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

    public static string AlertMessage(string propertyName, AlertEvent e)
    {
        var label = e.RuleType switch
        {
            RuleType.MissingData => "Data mangler",
            RuleType.SourceFailure or RuleType.TokenExpiry => "Teknisk alarm",
            _ => Da.Severity(e.Severity),
        };
        return $"Energy Monitor · {propertyName}\n{label}: {e.Explanation}";
    }
}
