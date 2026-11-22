using System.Globalization;
using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Core.Text;

namespace EnergyMonitor.Core.Reports;

/// <summary>The properties that pay the same price hour by hour, and that price for one day.</summary>
public sealed record PriceGroup(IReadOnlyList<string> PropertyNames, string Area, DayPrices Day, TimeZoneInfo Zone);

/// <summary>The Telegram texts about hourly prices. Amounts are DKK per kWh incl. VAT.</summary>
public static class PriceText
{
    /// <summary>"2,31"; a negative amount gets a real minus sign.</summary>
    public static string Kr(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',').Replace('-', '−');

    /// <summary>"kl. 02–05" in local time. A period that ends at midnight ends at 24.</summary>
    public static string Hours(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeZoneInfo zone)
    {
        var from = TimeZoneInfo.ConvertTime(fromUtc, zone);
        var to = TimeZoneInfo.ConvertTime(toUtc, zone);
        var end = to.Hour == 0 && to.Date > from.Date ? "24" : to.ToString("HH", CultureInfo.InvariantCulture);
        return $"kl. {from.ToString("HH", CultureInfo.InvariantCulture)}–{end}";
    }

    /// <summary>
    /// The line in the daily report, or null when the day's prices are not complete:
    /// "Elpris i dag (Sommerhus, DK1): gns. 2,31 kr./kWh. Resten af dagen: billigst kl. 13–16 (1,62), dyrest kl. 17–20 (3,71)."
    /// </summary>
    public static string? TodayLine(PriceGroup group)
    {
        if (group.Day.Average is not { } average)
        {
            return null;
        }

        var periods = Periods(group);
        return $"Elpris i dag ({string.Join(", ", group.PropertyNames)}, {group.Area}): gns. {Kr(average)} kr./kWh."
            + (periods is null ? "" : $" Resten af dagen: {periods}.");
    }

    /// <summary>
    /// Why a day is worth a message of its own: negative day-ahead prices, an average far from the recent
    /// days', or an hour at or above the high price. Empty for an ordinary day.
    /// </summary>
    /// <param name="recentAverage">The average of the preceding days, or null when it is not known.</param>
    /// <param name="deviationPercent">0 turns the comparison with the recent days off.</param>
    /// <param name="highPrice">DKK per kWh incl. VAT; 0 turns the high price off.</param>
    public static IReadOnlyList<string> Remarkable(PriceGroup group, decimal? recentAverage, decimal deviationPercent, decimal highPrice)
    {
        var day = group.Day;
        var reasons = new List<string>();
        if (day.Average is not { } average)
        {
            return reasons;
        }

        if (day.NegativeHours > 0)
        {
            reasons.Add(day.NegativeHours == 1 ? "1 time med negativ spotpris." : $"{day.NegativeHours} timer med negativ spotpris.");
        }

        if (deviationPercent > 0 && recentAverage is { } recent && recent > 0)
        {
            var deviation = (average - recent) / recent * 100;
            if (Math.Abs(deviation) >= deviationPercent)
            {
                reasons.Add($"Gennemsnittet er {Math.Round(Math.Abs(deviation), MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)} % "
                    + $"{(deviation < 0 ? "lavere" : "højere")} end de seneste dage ({Kr(recent)} kr./kWh).");
            }
        }

        if (highPrice > 0 && day.Max >= highPrice && day.Hours.FirstOrDefault(h => h.Total == day.Max) is { } peak)
        {
            reasons.Add($"Dyreste time: {Hours(peak.StartUtc, peak.EndUtc, group.Zone)}, {Kr(peak.Total!.Value)} kr./kWh.");
        }

        return reasons;
    }

    /// <summary>The message about tomorrow: one line per group, each followed by what makes it stand out.</summary>
    public static string TomorrowMessage(DateOnly date, IReadOnlyList<(PriceGroup Group, IReadOnlyList<string> Reasons)> groups)
    {
        var lines = new List<string> { $"Energy Monitor · elpriser i morgen, {Da.LongDate(date)}" };
        foreach (var (group, reasons) in groups)
        {
            var periods = Periods(group);
            lines.Add($"{string.Join(", ", group.PropertyNames)} ({group.Area}): gns. {Kr(group.Day.Average ?? 0)} kr./kWh."
                + (periods is null ? "" : $" {char.ToUpperInvariant(periods[0])}{periods[1..]}."));
            lines.AddRange(reasons);
        }

        return string.Join('\n', lines);
    }

    /// <summary>"billigst kl. 02–05 (1,47), dyrest kl. 17–20 (3,40)", or null when there is nothing to choose between.</summary>
    private static string? Periods(PriceGroup group) =>
        group.Day is { Cheapest: { } low, MostExpensive: { } high } && low.Average != high.Average
            ? $"billigst {Hours(low.FromUtc, low.ToUtc, group.Zone)} ({Kr(low.Average)}), dyrest {Hours(high.FromUtc, high.ToUtc, group.Zone)} ({Kr(high.Average)})"
            : null;
}
