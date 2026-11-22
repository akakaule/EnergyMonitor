using System.Text.Json.Serialization;

namespace EnergyMonitor.Core.Model;

/// <summary>A monitored property (Hjem, Sommerhus) linked to one metering point.</summary>
public sealed record Property(
    string Id,
    string Name,
    string MeteringPointId,
    bool MonitoringEnabled,
    string TimeZone,
    DateTimeOffset CreatedAt)
{
    public const string DefaultTimeZone = "Europe/Copenhagen";

    /// <summary>
    /// Where the property is, in decimal degrees; both or neither. Only used to fetch the outdoor
    /// temperature. Properties with initializers, so a property stored before they existed has none.
    /// </summary>
    public double? Latitude { get; init; }

    public double? Longitude { get; init; }
}

/// <summary>Quality of one source interval. <see cref="Missing"/> and <see cref="Incomplete"/> never carry a usable value.</summary>
public enum IntervalQuality
{
    Measured,
    Estimated,
    Adjusted,
    Missing,
    Incomplete,
}

public sealed record ConsumptionInterval(
    string PropertyId,
    string Source,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double? KWh,
    string Resolution,
    IntervalQuality Quality,
    DateTimeOffset FetchedAtUtc)
{
    /// <summary>True when the interval holds a value that may be summed.</summary>
    [JsonIgnore]
    public bool HasValue =>
        KWh is not null && Quality is IntervalQuality.Measured or IntervalQuality.Estimated or IntervalQuality.Adjusted;
}

/// <summary>
/// Coverage of one local calendar day. A day that should have had data long ago is reported as stale
/// at read time (see <c>PropertyStatusCalculator</c>); it is not a stored value.
/// </summary>
public enum DayQuality
{
    Complete,
    Partial,
    Missing,
}

/// <summary>
/// One local calendar day. <see cref="KWh"/> is null when nothing was received: missing is never zero.
/// </summary>
public sealed record DailyConsumption(
    string PropertyId,
    DateOnly LocalDate,
    double? KWh,
    int ExpectedIntervals,
    int ReceivedIntervals,
    DayQuality Quality,
    DateTimeOffset UpdatedAtUtc)
{
    [JsonIgnore]
    public bool IsComplete => Quality == DayQuality.Complete && KWh is not null;
}

public enum RuleType
{
    LowConsumption,
    HighConsumption,
    MissingData,
    SourceFailure,
    TokenExpiry,

    /// <summary>Heat pump: the indoor temperature is below the frost limit.</summary>
    IndoorTooCold,

    /// <summary>Heat pump: the device has lost its connection to Sensibo.</summary>
    HeatPumpOffline,

    /// <summary>System: Sensibo cannot be reached or rejects the API key.</summary>
    SensiboFailure,
}

/// <summary>Parameters of a rule. Only the fields relevant to the rule type are used.</summary>
public sealed record RuleParams
{
    /// <summary>Low: alarm below this percentage of the baseline. High: alarm above it.</summary>
    public double ThresholdPercent { get; init; }

    /// <summary>Relative rules are off while the baseline is below this many kWh per day.</summary>
    public double MinBaselineKWh { get; init; } = 3;

    public int BaselineDays { get; init; } = 14;

    public int MinBaselineDays { get; init; } = 7;

    /// <summary>Low consumption escalates from Observer to Alarm at this many consecutive days.</summary>
    public int EscalateAfterDays { get; init; } = 2;

    /// <summary>Missing data: hours without a new complete day.</summary>
    public int MissingHours { get; init; } = 72;

    /// <summary>Source failure: consecutive failed sync runs.</summary>
    public int ConsecutiveFailures { get; init; } = 3;

    /// <summary>Indoor too cold: raised below this temperature, closed again at this plus <c>HeatPumpRules.HysteresisC</c>.</summary>
    public double MinTemperatureC { get; init; } = 5;

    /// <summary>Indoor too cold escalates from Observer to Alarm after this many minutes.</summary>
    public int SustainedMinutes { get; init; } = 120;

    /// <summary>Heat pump offline: minutes since the device was last seen by Sensibo.</summary>
    public int OfflineMinutes { get; init; } = 60;

    /// <summary>
    /// Low and high consumption: compare the day with the consumption expected for its outdoor temperature
    /// instead of the median, on the days such an expectation exists. On, also for rules stored before it existed.
    /// </summary>
    public bool WeatherAdjusted { get; init; } = true;

    public static RuleParams DefaultFor(RuleType type) => type switch
    {
        RuleType.LowConsumption => new RuleParams { ThresholdPercent = 50 },
        RuleType.HighConsumption => new RuleParams { ThresholdPercent = 200 },
        _ => new RuleParams(),
    };
}

public sealed record AlertRule(
    string Id,
    string PropertyId,
    RuleType Type,
    bool Enabled,
    RuleParams Params,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>The rule types a property carries.</summary>
    public static readonly IReadOnlyList<RuleType> PropertyRuleTypes =
        [RuleType.LowConsumption, RuleType.HighConsumption, RuleType.MissingData];

    /// <summary>The extra rule types of a property with a linked heat pump.</summary>
    public static readonly IReadOnlyList<RuleType> HeatPumpRuleTypes =
        [RuleType.IndoorTooCold, RuleType.HeatPumpOffline];

    public static AlertRule Default(string propertyId, RuleType type, DateTimeOffset now) =>
        new(type.ToString(), propertyId, type, true, RuleParams.DefaultFor(type), now);
}

public enum AlertSeverity
{
    Observer = 1,
    Alarm = 2,
}

public enum AlertState
{
    Open,
    Closed,
}

/// <summary>
/// An alert event, unique on (property, rule type, first affected day, sequence). The observed value, baseline,
/// threshold and parameters are a snapshot taken when the event was raised or last escalated.
/// <see cref="Sequence"/> is 0 except for rules evaluated within the day (the heat pump), where a second
/// event on the same day gets 1, 2, …
/// </summary>
public sealed record AlertEvent(
    string PropertyId,
    string RuleId,
    RuleType RuleType,
    DateOnly FirstLocalDate,
    DateOnly LastLocalDate,
    AlertSeverity Severity,
    AlertState State,
    double? Observed,
    double? Baseline,
    double? Threshold,
    string Explanation,
    RuleParams ParamsSnapshot,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? AcknowledgedAtUtc = null,
    DateTimeOffset? LastNotifiedAtUtc = null,
    AlertSeverity? LastNotifiedSeverity = null,
    DateTimeOffset? ClosedAtUtc = null,
    int Sequence = 0)
{
    /// <summary>Property id used for events about the data source itself.</summary>
    public const string SystemPropertyId = "system";

    /// <summary>
    /// Set when a consumption rule judged the day against what its outdoor temperature (°C) leads one to
    /// expect: <see cref="Baseline"/> is then that expected consumption, not a median.
    /// </summary>
    public double? OutdoorC { get; init; }

    [JsonIgnore]
    public string Id => MakeId(PropertyId, RuleType, FirstLocalDate, Sequence);

    public static string MakeId(string propertyId, RuleType type, DateOnly firstDate, int sequence = 0) =>
        sequence == 0 ? $"{propertyId}~{type}~{firstDate:yyyy-MM-dd}" : $"{propertyId}~{type}~{firstDate:yyyy-MM-dd}~{sequence}";

    public static bool TryParseId(string id, out string propertyId, out RuleType type, out DateOnly firstDate, out int sequence)
    {
        propertyId = "";
        type = default;
        firstDate = default;
        sequence = 0;
        var parts = id.Split('~');
        if (parts.Length is not (3 or 4) || parts[0].Length == 0)
        {
            return false;
        }

        propertyId = parts[0];
        return Enum.TryParse(parts[1], out type)
            && DateOnly.TryParseExact(parts[2], "yyyy-MM-dd", out firstDate)
            && (parts.Length == 3 || (int.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out sequence) && sequence > 0));
    }
}

public enum SyncStatus
{
    Running,
    Succeeded,
    Failed,
}

public sealed record SyncRun(
    string Id,
    string Source,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    SyncStatus Status,
    int IntervalCount,
    string? ErrorCode);

public static class UserRoles
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";
}

public sealed record User(string Id, string IdentityProviderSubject, string Role);

/// <summary>
/// Source-neutral measurement (spec §13), so later sources such as Home Assistant temperature sensors
/// fit the same pipeline. For interval metrics <see cref="ObservedAtUtc"/> is the interval start.
/// </summary>
public sealed record Measurement(
    string SourceId,
    string PropertyId,
    string Metric,
    double? Value,
    string Unit,
    DateTimeOffset ObservedAtUtc,
    IntervalQuality Quality,
    DateTimeOffset? IntervalEndUtc = null,
    string? Resolution = null)
{
    public const string EnergyConsumption = "energy.consumption";
}

/// <summary>
/// The latest state of a heat pump as Sensibo reports it. Sensibo measures in Celsius whatever the
/// display unit; <see cref="TargetTemperature"/> is in <see cref="TemperatureUnit"/>.
/// </summary>
public sealed record HeatPumpReading(
    string DeviceId,
    string Name,
    double? TemperatureC,
    double? Humidity,
    DateTimeOffset? MeasuredAtUtc,
    bool Online,
    DateTimeOffset? LastSeenUtc,
    bool? On = null,
    string? Mode = null,
    double? TargetTemperature = null,
    string? TemperatureUnit = null);

public sealed record NotificationMessage(string Text);

/// <summary>
/// A queued notification. <see cref="DeliveredTo"/> and <see cref="RejectedBy"/> are the recipients that are
/// done with it, so a retry after a partial failure never sends the message to anyone twice.
/// </summary>
public sealed record OutboxMessage(
    string Id,
    string Text,
    DateTimeOffset CreatedAtUtc,
    int Attempts = 0,
    string? LastError = null,
    IReadOnlyList<string>? DeliveredTo = null,
    IReadOnlyList<string>? RejectedBy = null);

public sealed record NotificationSettings(bool DailyReportEnabled, IReadOnlyList<string> Recipients)
{
    public static readonly NotificationSettings Default = new(true, []);

    /// <summary>
    /// Send tomorrow's electricity prices when they stand out. A property with an initializer, so settings
    /// stored before it existed read as on.
    /// </summary>
    public bool TomorrowPricesEnabled { get; init; } = true;
}
