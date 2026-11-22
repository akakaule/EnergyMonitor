using System.Text.Json;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Alerts;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Reports;
using EnergyMonitor.Core.Time;
using EnergyMonitor.Web.Eloverblik;
using EnergyMonitor.Web.HeatPump;
using EnergyMonitor.Web.Storage;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Web.Jobs;

public sealed class ScheduleOptions
{
    public bool Enabled { get; set; } = true;

    public string TimeZone { get; set; } = Property.DefaultTimeZone;

    /// <summary>Local times of the daily syncs.</summary>
    public string[] SyncTimes { get; set; } = ["08:00", "17:00"];

    /// <summary>Local time of the daily report; after the morning sync.</summary>
    public string ReportTime { get; set; } = "09:00";

    /// <summary>
    /// Local time of the extra fetch of day-ahead prices, after tomorrow's are published around 13:00.
    /// Empty turns it off; it only runs with <c>Pricing:Enabled</c>.
    /// </summary>
    public string PriceSyncTime { get; set; } = "13:30";

    public int TickSeconds { get; set; } = 60;

    /// <summary>A slot missed while the app was down is still run if it is no older than this.</summary>
    public int CatchUpHours { get; set; } = 6;
}

public sealed class NotificationSettingsService(IStateStore state, IOptionsMonitor<Notifications.TelegramOptions> telegram)
{
    private const string Key = "settings|notifications";

    public async Task<NotificationSettings> GetAsync(CancellationToken ct) =>
        await state.GetAsync(Key, ct) is { } json
            ? JsonSerializer.Deserialize<NotificationSettings>(json, StoreJson.Options) ?? NotificationSettings.Default
            : NotificationSettings.Default with { Recipients = telegram.CurrentValue.ChatIds };

    public Task SetAsync(NotificationSettings settings, CancellationToken ct) =>
        state.SetAsync(Key, StoreJson.Write(settings), ct);
}

/// <summary>Loads what the dashboard and the report both need for each property.</summary>
public sealed class SummaryService(
    IPropertyStore properties,
    IConsumptionStore consumption,
    IAlertStore alerts,
    ISyncRunStore runs,
    HeatPumpStateService heatPumps,
    IOptionsMonitor<SensiboOptions> sensibo,
    TimeProvider time)
{
    public const int HistoryDays = 60;

    public async Task<IReadOnlyList<AlertRule>> GetRulesAsync(string propertyId, CancellationToken ct)
    {
        var stored = await alerts.GetRulesAsync(propertyId, ct);
        var now = time.GetUtcNow();
        IReadOnlyList<RuleType> types = sensibo.CurrentValue.Enabled && (await heatPumps.GetLinksAsync(ct)).Any(l => l.PropertyId == propertyId)
            ? [.. AlertRule.PropertyRuleTypes, .. AlertRule.HeatPumpRuleTypes]
            : AlertRule.PropertyRuleTypes;
        return [.. types.Select(t => stored.FirstOrDefault(r => r.Type == t) ?? AlertRule.Default(propertyId, t, now))];
    }

    public async Task<(PropertySummary Summary, IReadOnlyList<DailyConsumption> Days)> GetAsync(Property property, CancellationToken ct)
    {
        var zone = LocalDay.Zone(property.TimeZone);
        var now = time.GetUtcNow();
        var today = LocalDay.Of(now, zone);
        var days = await consumption.GetDailyAsync(property.Id, today.AddDays(-HistoryDays), today, ct);
        var events = await alerts.GetEventsAsync(property.Id, ct);
        var rules = await GetRulesAsync(property.Id, ct);
        var baselineParams = rules.First(r => r.Type == RuleType.LowConsumption).Params;
        return (PropertySummaryCalculator.Calculate(property, days, events, baselineParams, now, zone), days);
    }

    public async Task<IReadOnlyList<PropertySummary>> GetAllAsync(CancellationToken ct)
    {
        var list = new List<PropertySummary>();
        foreach (var property in await properties.ListAsync(ct))
        {
            list.Add((await GetAsync(property, ct)).Summary);
        }

        return list;
    }

    public async Task<SyncRun?> LastSuccessfulSyncAsync(CancellationToken ct) =>
        (await runs.GetRecentAsync(EloverblikMeasurementSource.Id, 20, ct)).FirstOrDefault(r => r.Status == SyncStatus.Succeeded);
}

/// <summary>
/// Evaluates the rules after a sync, stores new and changed events, and queues one message for each
/// event that is new or has risen in severity.
/// </summary>
public sealed partial class AlertService(
    IPropertyStore properties,
    IConsumptionStore consumption,
    IAlertStore alerts,
    ISyncRunStore runs,
    IOutboxStore outbox,
    SummaryService summaries,
    EloverblikTokenStatus token,
    HeatPumpStateService heatPumps,
    EnergyMonitor.Web.Weather.HeatingQueryService heating,
    TimeProvider time,
    IOptions<SyncOptions> syncOptions,
    IOptions<ScheduleOptions> schedule,
    ILogger<AlertService> logger)
{
    public async Task EvaluateAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var property in await properties.ListAsync(ct))
        {
            var zone = LocalDay.Zone(property.TimeZone);
            var today = LocalDay.Of(now, zone);
            var days = await consumption.GetDailyAsync(property.Id, today.AddDays(-SummaryService.HistoryDays), today, ct);
            var rules = await summaries.GetRulesAsync(property.Id, ct);
            var existing = await alerts.GetEventsAsync(property.Id, ct);

            // Only the days a sync can still change are analysed, so the first 90-day import cannot
            // raise alarms about weeks-old consumption.
            var from = today.AddDays(-syncOptions.Value.RefetchDays);
            var weatherRules = property.MonitoringEnabled && rules.Any(r =>
                r.Type is RuleType.LowConsumption or RuleType.HighConsumption && r.Enabled && r.Params.WeatherAdjusted);
            var expected = weatherRules ? await heating.ExpectationsAsync(property, from, today, ct) : null;
            var result = RuleEngine.EvaluateProperty(property, rules, days, existing, from, now, zone, expected);
            await ApplyAsync(property.Id, property.Name, result, ct);
        }

        var systemZone = LocalDay.Zone(schedule.Value.TimeZone);
        var recent = await runs.GetRecentAsync(EloverblikMeasurementSource.Id, 10, ct);
        var systemEvents = await alerts.GetEventsAsync(AlertEvent.SystemPropertyId, ct);
        var source = RuleEngine.EvaluateSource(recent, token.ExpiresAt, systemEvents, RuleParams.DefaultFor(RuleType.SourceFailure), now, systemZone);
        await ApplyAsync(AlertEvent.SystemPropertyId, "Datakilde", source, ct);
    }

    /// <summary>
    /// Heat pump rules after a collection. A failed collection only feeds the Sensibo source rule: the last
    /// readings are stale then, and judging them would raise false house alarms.
    /// </summary>
    public async Task EvaluateHeatPumpAsync(HeatPumpStatus status, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (status.ConsecutiveFailures == 0)
        {
            var links = await heatPumps.GetLinksAsync(ct);
            foreach (var property in await properties.ListAsync(ct))
            {
                // No reading (not linked, or the pump is gone from Sensibo) closes what is still open.
                var deviceId = links.FirstOrDefault(l => l.PropertyId == property.Id)?.DeviceId;
                var reading = status.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
                var rules = await summaries.GetRulesAsync(property.Id, ct);
                var existing = await alerts.GetEventsAsync(property.Id, ct);
                var result = HeatPumpRules.EvaluateProperty(property, rules, reading, existing, now, LocalDay.Zone(property.TimeZone));
                await ApplyAsync(property.Id, property.Name, result, ct);
            }
        }

        var systemEvents = await alerts.GetEventsAsync(AlertEvent.SystemPropertyId, ct);
        var source = HeatPumpRules.EvaluateSource(
            status.ConsecutiveFailures, status.LastError, systemEvents, RuleParams.DefaultFor(RuleType.SensiboFailure), now, LocalDay.Zone(schedule.Value.TimeZone));
        await ApplyAsync(AlertEvent.SystemPropertyId, "Datakilde", source, ct);
    }

    private async Task ApplyAsync(string propertyId, string propertyName, RuleEvaluation result, CancellationToken ct)
    {
        foreach (var created in result.Created)
        {
            if (await alerts.TryCreateEventAsync(created, ct))
            {
                LogEvent(logger, "raised", created.Id, created.Severity);
            }
        }

        foreach (var updated in result.Updated)
        {
            await alerts.UpdateEventAsync(updated, ct);
            LogEvent(logger, updated.State == AlertState.Closed ? "closed" : "updated", updated.Id, updated.Severity);
        }

        // Read back what is stored, so an event whose message could not be queued earlier is retried
        // here, and one that another run already handled is left alone.
        foreach (var e in (await alerts.GetEventsAsync(propertyId, ct)).Where(RuleEngine.NeedsNotification))
        {
            var message = new OutboxMessage($"alert|{e.Id}|{e.Severity}", ReportComposer.AlertMessage(propertyName, e), time.GetUtcNow());
            await outbox.TryEnqueueAsync(message, ct);
            await alerts.UpdateEventAsync(e with { LastNotifiedAtUtc = time.GetUtcNow(), LastNotifiedSeverity = e.Severity }, ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Alert event {Action}: {EventId} ({Severity})")]
    private static partial void LogEvent(ILogger logger, string action, string eventId, AlertSeverity severity);
}
