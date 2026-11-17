using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;

namespace EnergyMonitor.Web.Storage;

internal static class StoreJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(Row row) => JsonSerializer.Deserialize<T>(row.Json, Options)
        ?? throw new InvalidOperationException($"Stored {typeof(T).Name} could not be read.");

    public static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Minute(DateTimeOffset instant) => instant.UtcDateTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
}

public sealed class PropertyStore(ITableBackend tables) : IPropertyStore
{
    private const string Table = "properties";
    private const string Pk = "property";

    public async Task<IReadOnlyList<Property>> ListAsync(CancellationToken ct) =>
        [.. (await tables.QueryAsync(Table, Pk, null, null, null, ct)).Select(StoreJson.Read<Property>).OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)];

    public async Task<Property?> GetAsync(string id, CancellationToken ct) =>
        await tables.GetAsync(Table, Pk, id, ct) is { } row ? StoreJson.Read<Property>(row) : null;

    public Task UpsertAsync(Property property, CancellationToken ct) =>
        tables.UpsertAsync(Table, [new Row(Pk, property.Id, StoreJson.Write(property))], ct);
}

/// <summary>
/// Intervals: partition <c>{property}|{source}|{yyyyMM}</c>, row <c>{start}|{end}</c> — the identity
/// (metering point, interval start, interval end, source), so a repeated or corrected interval replaces
/// the stored one. Days: partition <c>{property}</c>, row <c>yyyy-MM-dd</c>.
/// </summary>
public sealed class ConsumptionStore(ITableBackend tables) : IConsumptionStore
{
    private const string Intervals = "intervals";
    private const string Daily = "daily";

    private static string IntervalPk(string propertyId, string source, DateTimeOffset start) =>
        $"{propertyId}|{source}|{start.UtcDateTime:yyyyMM}";

    public Task UpsertIntervalsAsync(IReadOnlyCollection<ConsumptionInterval> intervals, CancellationToken ct) =>
        tables.UpsertAsync(
            Intervals,
            [.. intervals.Select(i => new Row(
                IntervalPk(i.PropertyId, i.Source, i.StartUtc),
                $"{StoreJson.Minute(i.StartUtc)}|{StoreJson.Minute(i.EndUtc)}",
                StoreJson.Write(i)))],
            ct);

    public async Task<IReadOnlyList<ConsumptionInterval>> GetIntervalsAsync(string propertyId, string source, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var result = new List<ConsumptionInterval>();
        var month = new DateTimeOffset(fromUtc.UtcDateTime.Year, fromUtc.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
        for (; month < toUtc; month = month.AddMonths(1))
        {
            var rows = await tables.QueryAsync(Intervals, IntervalPk(propertyId, source, month), StoreJson.Minute(fromUtc), StoreJson.Minute(toUtc), null, ct);
            result.AddRange(rows.Select(StoreJson.Read<ConsumptionInterval>));
        }

        return result;
    }

    public Task UpsertDailyAsync(IReadOnlyCollection<DailyConsumption> days, CancellationToken ct) =>
        tables.UpsertAsync(Daily, [.. days.Select(d => new Row(d.PropertyId, StoreJson.Day(d.LocalDate), StoreJson.Write(d)))], ct);

    public async Task<IReadOnlyList<DailyConsumption>> GetDailyAsync(string propertyId, DateOnly from, DateOnly to, CancellationToken ct) =>
        [.. (await tables.QueryAsync(Daily, propertyId, StoreJson.Day(from), StoreJson.Day(to.AddDays(1)), null, ct)).Select(StoreJson.Read<DailyConsumption>)];

    public async Task DeleteAllAsync(string propertyId, CancellationToken ct)
    {
        await tables.DeleteAsync(Intervals, await tables.QueryPartitionsAsync(Intervals, $"{propertyId}|", $"{propertyId}|~", ct), ct);
        await tables.DeleteAsync(Daily, await tables.QueryAsync(Daily, propertyId, null, null, null, ct), ct);
    }
}

/// <summary>
/// Events: partition <c>{property}</c>, row <c>{ruleType}|{firstDay}</c> (plus <c>|{sequence}</c> for a second
/// event of a heat pump rule on the same day). Creation is insert-if-absent,
/// which is what stops a repeated sync or a restart from raising the same event twice.
/// </summary>
public sealed class AlertStore(ITableBackend tables) : IAlertStore
{
    private const string Rules = "rules";
    private const string Events = "events";

    private static string EventRk(RuleType type, DateOnly first, int sequence) =>
        sequence == 0 ? $"{type}|{StoreJson.Day(first)}" : $"{type}|{StoreJson.Day(first)}|{sequence}";

    public async Task<IReadOnlyList<AlertRule>> GetRulesAsync(string propertyId, CancellationToken ct) =>
        [.. (await tables.QueryAsync(Rules, propertyId, null, null, null, ct)).Select(StoreJson.Read<AlertRule>)];

    public Task UpsertRuleAsync(AlertRule rule, CancellationToken ct) =>
        tables.UpsertAsync(Rules, [new Row(rule.PropertyId, rule.Id, StoreJson.Write(rule))], ct);

    public async Task<IReadOnlyList<AlertEvent>> GetEventsAsync(string propertyId, CancellationToken ct) =>
        [.. (await tables.QueryAsync(Events, propertyId, null, null, null, ct)).Select(StoreJson.Read<AlertEvent>)];

    public async Task<AlertEvent?> GetEventAsync(string id, CancellationToken ct) =>
        AlertEvent.TryParseId(id, out var propertyId, out var type, out var first, out var sequence)
        && await tables.GetAsync(Events, propertyId, EventRk(type, first, sequence), ct) is { } row
            ? StoreJson.Read<AlertEvent>(row)
            : null;

    public Task<bool> TryCreateEventAsync(AlertEvent alertEvent, CancellationToken ct) =>
        tables.TryAddAsync(Events, ToRow(alertEvent), ct);

    public Task UpdateEventAsync(AlertEvent alertEvent, CancellationToken ct) =>
        tables.UpsertAsync(Events, [ToRow(alertEvent)], ct);

    public async Task DeleteAllAsync(string propertyId, CancellationToken ct) =>
        await tables.DeleteAsync(Events, await tables.QueryAsync(Events, propertyId, null, null, null, ct), ct);

    private static Row ToRow(AlertEvent e) => new(e.PropertyId, EventRk(e.RuleType, e.FirstLocalDate, e.Sequence), StoreJson.Write(e));
}

public sealed class SyncRunStore(ITableBackend tables) : ISyncRunStore
{
    private const string Table = "syncruns";

    // Inverted ticks sort the newest run first.
    private static string Rk(SyncRun run) =>
        $"{DateTimeOffset.MaxValue.UtcTicks - run.StartedAtUtc.UtcTicks:D19}|{run.Id}";

    public Task SaveAsync(SyncRun run, CancellationToken ct) =>
        tables.UpsertAsync(Table, [new Row(run.Source, Rk(run), StoreJson.Write(run))], ct);

    public async Task<IReadOnlyList<SyncRun>> GetRecentAsync(string source, int count, CancellationToken ct) =>
        [.. (await tables.QueryAsync(Table, source, null, null, count, ct)).Select(StoreJson.Read<SyncRun>)];
}

public sealed class StateStore(ITableBackend tables) : IStateStore
{
    private const string Table = "state";
    private const string Pk = "state";

    public async Task<string?> GetAsync(string key, CancellationToken ct) =>
        (await tables.GetAsync(Table, Pk, key, ct))?.Json;

    public Task SetAsync(string key, string value, CancellationToken ct) =>
        tables.UpsertAsync(Table, [new Row(Pk, key, value)], ct);

    public Task<bool> TryClaimAsync(string key, CancellationToken ct) =>
        tables.TryAddAsync(Table, new Row(Pk, key, "\"claimed\""), ct);

    public Task DeleteAsync(string key, CancellationToken ct) =>
        tables.DeleteAsync(Table, [new Row(Pk, key, "")], ct);
}

/// <summary>
/// Pending and sent messages share one partition. A sent message stays as a marker, so the same message
/// id can never be queued a second time.
/// </summary>
public sealed class OutboxStore(ITableBackend tables) : IOutboxStore
{
    private const string Table = "outbox";
    private const string Pk = "outbox";
    private const string Pending = "pending";
    private const string Sent = "sent";

    public Task<bool> TryEnqueueAsync(OutboxMessage message, CancellationToken ct) =>
        tables.TryAddAsync(Table, new Row(Pk, message.Id, StoreJson.Write(message), Pending), ct);

    public async Task<IReadOnlyList<OutboxMessage>> ListPendingAsync(CancellationToken ct) =>
        [.. (await tables.QueryAsync(Table, Pk, null, null, null, ct))
            .Where(r => r.Tag == Pending)
            .Select(StoreJson.Read<OutboxMessage>)
            .OrderBy(m => m.CreatedAtUtc)];

    public async Task MarkSentAsync(string id, CancellationToken ct)
    {
        if (await tables.GetAsync(Table, Pk, id, ct) is { } row)
        {
            await tables.UpsertAsync(Table, [row with { Tag = Sent }], ct);
        }
    }

    public async Task RecordFailureAsync(string id, string error, IReadOnlyList<string> deliveredTo, IReadOnlyList<string> rejectedBy, CancellationToken ct)
    {
        if (await tables.GetAsync(Table, Pk, id, ct) is { Tag: Pending } row)
        {
            var message = StoreJson.Read<OutboxMessage>(row);
            var updated = message with { Attempts = message.Attempts + 1, LastError = error, DeliveredTo = deliveredTo, RejectedBy = rejectedBy };
            await tables.UpsertAsync(Table, [row with { Json = StoreJson.Write(updated) }], ct);
        }
    }
}
