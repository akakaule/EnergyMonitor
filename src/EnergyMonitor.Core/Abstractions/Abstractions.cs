using EnergyMonitor.Core.Model;

namespace EnergyMonitor.Core.Abstractions;

/// <summary>A source of measurements for a property (Eloverblik in v1; Home Assistant later).</summary>
public interface IMeasurementSource
{
    string SourceId { get; }

    /// <summary>
    /// Fetches measurements for local days from <paramref name="fromLocal"/> up to, but not necessarily
    /// including, <paramref name="toLocal"/>. An empty list means the source has no data for the period.
    /// </summary>
    Task<IReadOnlyList<Measurement>> FetchAsync(Property property, DateOnly fromLocal, DateOnly toLocal, CancellationToken ct);
}

/// <summary>Thrown by a source with a short, secret-free code that is safe to store and show.</summary>
public class MeasurementSourceException(string errorCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string ErrorCode { get; } = errorCode;

    public const string TokenInvalid = "token_invalid";
}

public interface INotificationChannel
{
    string Name { get; }

    /// <summary>True when the channel has what it needs (credentials, recipients) to send.</summary>
    bool IsConfigured(NotificationSettings settings);

    /// <summary>Sends to every recipient. Throws <see cref="NotificationException"/> when delivery fails.</summary>
    Task SendAsync(NotificationMessage message, NotificationSettings settings, CancellationToken ct);
}

public sealed class NotificationException(string message, Exception? inner = null) : Exception(message, inner);

public interface IPropertyStore
{
    Task<IReadOnlyList<Property>> ListAsync(CancellationToken ct);

    Task<Property?> GetAsync(string id, CancellationToken ct);

    Task UpsertAsync(Property property, CancellationToken ct);
}

public interface IConsumptionStore
{
    /// <summary>Idempotent on (property, source, start, end); a repeated interval replaces the stored one.</summary>
    Task UpsertIntervalsAsync(IReadOnlyCollection<ConsumptionInterval> intervals, CancellationToken ct);

    Task<IReadOnlyList<ConsumptionInterval>> GetIntervalsAsync(string propertyId, string source, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    Task UpsertDailyAsync(IReadOnlyCollection<DailyConsumption> days, CancellationToken ct);

    /// <summary>Days from <paramref name="from"/> to <paramref name="to"/>, both inclusive, ascending.</summary>
    Task<IReadOnlyList<DailyConsumption>> GetDailyAsync(string propertyId, DateOnly from, DateOnly to, CancellationToken ct);

    Task DeleteAllAsync(string propertyId, CancellationToken ct);
}

public interface IAlertStore
{
    Task<IReadOnlyList<AlertRule>> GetRulesAsync(string propertyId, CancellationToken ct);

    Task UpsertRuleAsync(AlertRule rule, CancellationToken ct);

    Task<IReadOnlyList<AlertEvent>> GetEventsAsync(string propertyId, CancellationToken ct);

    Task<AlertEvent?> GetEventAsync(string id, CancellationToken ct);

    /// <summary>Inserts the event unless one with the same identity exists. Returns false when it does.</summary>
    Task<bool> TryCreateEventAsync(AlertEvent alertEvent, CancellationToken ct);

    Task UpdateEventAsync(AlertEvent alertEvent, CancellationToken ct);

    Task DeleteAllAsync(string propertyId, CancellationToken ct);
}

public interface ISyncRunStore
{
    Task SaveAsync(SyncRun run, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<SyncRun>> GetRecentAsync(string source, int count, CancellationToken ct);
}

/// <summary>Small key/value state: job markers, settings.</summary>
public interface IStateStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);

    Task SetAsync(string key, string value, CancellationToken ct);

    /// <summary>Atomically records the key. Returns false when it was already recorded.</summary>
    Task<bool> TryClaimAsync(string key, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

public interface IOutboxStore
{
    /// <summary>Queues the message unless one with the same id was ever queued. Returns false when it was.</summary>
    Task<bool> TryEnqueueAsync(OutboxMessage message, CancellationToken ct);

    Task<IReadOnlyList<OutboxMessage>> ListPendingAsync(CancellationToken ct);

    Task MarkSentAsync(string id, CancellationToken ct);

    Task RecordFailureAsync(string id, string error, CancellationToken ct);
}
