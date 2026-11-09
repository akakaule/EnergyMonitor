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
/// An alert event, unique on (property, rule type, first affected day). The observed value, baseline,
/// threshold and parameters are a snapshot taken when the event was raised or last escalated.
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
    DateTimeOffset? ClosedAtUtc = null)
{
    /// <summary>Property id used for events about the data source itself.</summary>
    public const string SystemPropertyId = "system";

    [JsonIgnore]
    public string Id => MakeId(PropertyId, RuleType, FirstLocalDate);

    public static string MakeId(string propertyId, RuleType type, DateOnly firstDate) =>
        $"{propertyId}~{type}~{firstDate:yyyy-MM-dd}";

    public static bool TryParseId(string id, out string propertyId, out RuleType type, out DateOnly firstDate)
    {
        propertyId = "";
        type = default;
        firstDate = default;
        var parts = id.Split('~');
        if (parts.Length != 3 || parts[0].Length == 0)
        {
            return false;
        }

        propertyId = parts[0];
        return Enum.TryParse(parts[1], out type)
            && DateOnly.TryParseExact(parts[2], "yyyy-MM-dd", out firstDate);
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

public sealed record NotificationMessage(string Text);

public sealed record OutboxMessage(
    string Id,
    string Text,
    DateTimeOffset CreatedAtUtc,
    int Attempts = 0,
    string? LastError = null);

public sealed record NotificationSettings(bool DailyReportEnabled, IReadOnlyList<string> Recipients)
{
    public static readonly NotificationSettings Default = new(true, []);
}
