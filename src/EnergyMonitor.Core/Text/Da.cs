using System.Globalization;
using EnergyMonitor.Core.Model;

namespace EnergyMonitor.Core.Text;

/// <summary>Danish formatting that does not depend on the culture data installed on the host.</summary>
public static class Da
{
    private static readonly string[] Months =
    [
        "januar", "februar", "marts", "april", "maj", "juni",
        "juli", "august", "september", "oktober", "november", "december",
    ];

    public static string KWh(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " kWh";

    public static string Percent(double value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + " %";

    public static string ShortDate(DateOnly date) => $"{date.Day}/{date.Month}";

    public static string LongDate(DateOnly date) => $"{date.Day}. {Months[date.Month - 1]}";

    public static string Time(DateTimeOffset local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Severity(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Alarm => "Alarm",
        _ => "Observer",
    };
}
