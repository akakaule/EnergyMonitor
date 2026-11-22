using System.Globalization;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Reports;
using EnergyMonitor.Core.Time;
using EnergyMonitor.Web.HeatPump;
using EnergyMonitor.Web.Storage;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Web.Jobs;

/// <summary>Sends queued messages. A failed delivery stays queued and is tried again on a later pass.</summary>
public sealed partial class OutboxDispatcher(
    IOutboxStore outbox,
    INotificationChannel channel,
    NotificationSettingsService settings,
    TimeProvider time,
    ILogger<OutboxDispatcher> logger)
{
    /// <summary>A message nobody could be told about for this long is dropped rather than sent late.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(72);

    // The scheduler tick, the sync pipeline and the heat pump collector all dispatch. One at a time, so two
    // of them never read the same pending message and both send it.
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await DispatchPendingAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> DispatchPendingAsync(CancellationToken ct)
    {
        var pending = await outbox.ListPendingAsync(ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        var current = await settings.GetAsync(ct);
        var sent = 0;
        foreach (var message in pending)
        {
            if (time.GetUtcNow() - message.CreatedAtUtc > MaxAge)
            {
                await outbox.MarkSentAsync(message.Id, ct);
                LogExpired(logger, message.Id);
                continue;
            }

            if (!channel.IsConfigured(current))
            {
                continue;
            }

            // Each recipient gets the message once: those it was delivered to, or who rejected it for good,
            // are skipped when a partial failure is retried.
            var delivered = (message.DeliveredTo ?? []).ToList();
            var rejected = (message.RejectedBy ?? []).ToList();
            string? retryable = null;
            foreach (var recipient in current.Recipients.Where(r => !delivered.Contains(r) && !rejected.Contains(r)))
            {
                try
                {
                    await channel.SendAsync(new NotificationMessage(message.Text), recipient, ct);
                    delivered.Add(recipient);
                }
                catch (NotificationException ex) when (ex.Permanent)
                {
                    rejected.Add(recipient);
                    LogRejected(logger, message.Id, recipient, ex.Message);
                }
                catch (NotificationException ex)
                {
                    retryable ??= ex.Message;
                }
            }

            if (retryable is null)
            {
                await outbox.MarkSentAsync(message.Id, ct);
                sent++;
            }
            else
            {
                await outbox.RecordFailureAsync(message.Id, retryable, delivered, rejected, ct);
                LogFailed(logger, message.Id, message.Attempts + 1, retryable);
            }
        }

        return sent;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {MessageId} failed (attempt {Attempt}) and stays queued: {Reason}")]
    private static partial void LogFailed(ILogger logger, string messageId, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {MessageId} was rejected by recipient {Recipient} and is not retried for it: {Reason}")]
    private static partial void LogRejected(ILogger logger, string messageId, string recipient, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {MessageId} was never delivered and has expired")]
    private static partial void LogExpired(ILogger logger, string messageId);
}

public sealed class ReportService(
    SummaryService summaries,
    IAlertStore alerts,
    IOutboxStore outbox,
    NotificationSettingsService settings,
    HeatPumpStateService heatPumps,
    IOptionsMonitor<SensiboOptions> sensibo,
    EnergyMonitor.Web.Pricing.PriceOutlookService prices,
    TimeProvider time,
    IOptions<ScheduleOptions> schedule)
{
    public async Task<string> ComposeAsync(CancellationToken ct)
    {
        var zone = LocalDay.Zone(schedule.Value.TimeZone);
        var now = time.GetUtcNow();
        var lastSync = await summaries.LastSuccessfulSyncAsync(ct);
        return ReportComposer.DailyReport(
            LocalDay.Of(now, zone),
            await summaries.GetAllAsync(ct),
            await alerts.GetEventsAsync(AlertEvent.SystemPropertyId, ct),
            lastSync?.FinishedAtUtc is { } finished ? TimeZoneInfo.ConvertTime(finished, zone) : null,
            sensibo.CurrentValue.Enabled ? await heatPumps.LinkedReadingsAsync(ct) : null,
            await prices.TodayLinesAsync(ct));
    }

    /// <summary>Queues today's report. The message id is the date, so a day gets one report however often this runs.</summary>
    public async Task<bool> EnqueueDailyAsync(CancellationToken ct)
    {
        if (!(await settings.GetAsync(ct)).DailyReportEnabled)
        {
            return false;
        }

        var today = LocalDay.Of(time.GetUtcNow(), LocalDay.Zone(schedule.Value.TimeZone));
        return await outbox.TryEnqueueAsync(
            new OutboxMessage($"report|{StoreJson.Day(today)}", await ComposeAsync(ct), time.GetUtcNow()), ct);
    }
}

/// <summary>
/// Sync, outdoor temperature, then rules, then delivery: the order every scheduled or manual run follows.
/// The temperature comes before the rules because they judge the newest days by it.
/// </summary>
public sealed partial class MonitorPipeline(
    SyncService sync,
    EnergyMonitor.Web.Pricing.PriceSyncService prices,
    EnergyMonitor.Web.Pricing.PriceOutlookService outlook,
    EnergyMonitor.Web.Weather.WeatherSyncService weather,
    AlertService alerts,
    OutboxDispatcher dispatcher,
    ILogger<MonitorPipeline> logger)
{
    public async Task<SyncRun?> RunSyncAsync(CancellationToken ct)
    {
        var run = await sync.RunAsync(ct);
        if (run is null)
        {
            return null;
        }

        await weather.RunAsync(ct);

        // Rules run after a failed sync too: missing data and source failures are found there.
        await alerts.EvaluateAsync(ct);
        await dispatcher.DispatchAsync(ct);
        await RunPricesAsync(spotOnly: false, ct);
        return run;
    }

    /// <summary>Fetches prices, then sends the message about tomorrow if its prices are in and stand out.</summary>
    public async Task RunPricesAsync(bool spotOnly, CancellationToken ct)
    {
        await prices.RunAsync(ct, spotOnly);
        if (await outlook.EnqueueTomorrowAsync(ct))
        {
            await dispatcher.DispatchAsync(ct);
        }
    }

    /// <summary>Starts a run that outlives the request that asked for it.</summary>
    public void StartInBackground(CancellationToken stopping) => _ = Task.Run(
        async () =>
        {
            try
            {
                await RunSyncAsync(stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        },
        stopping);

    [LoggerMessage(Level = LogLevel.Error, Message = "Manual sync failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}

/// <summary>
/// The one background loop. Each minute it runs the slots that are due in local time. A slot is claimed
/// in storage before it runs, so a restart neither repeats a slot nor loses one that fell due while the
/// app was down. The heat pump is collected every 15 minutes on the same loop. With pricing on, the
/// day-ahead prices get a slot of their own in the afternoon, when tomorrow's have been published.
/// </summary>
public sealed partial class SchedulerService(
    MonitorPipeline pipeline,
    ReportService reports,
    HeatPumpCollector heatPump,
    OutboxDispatcher dispatcher,
    IStateStore state,
    TimeProvider time,
    IOptions<ScheduleOptions> options,
    IOptions<EnergyMonitor.Web.Pricing.PricingOptions> pricing,
    ILogger<SchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(o.TickSeconds), time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTickFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var o = options.Value;
        var zone = LocalDay.Zone(o.TimeZone);
        var now = time.GetUtcNow();
        var today = LocalDay.Of(now, zone);

        var slots = o.SyncTimes.Select(t => (Name: "sync", Time: ParseTime(t)))
            .Append((Name: "report", Time: ParseTime(o.ReportTime)))
            .Concat(pricing.Value.Enabled && !string.IsNullOrWhiteSpace(o.PriceSyncTime) ? [(Name: "prices", Time: ParseTime(o.PriceSyncTime))] : [])
            .OrderBy(s => s.Time);

        foreach (var (name, slotTime) in slots)
        {
            var due = LocalDay.At(today, slotTime, zone);
            if (now < due || now - due > TimeSpan.FromHours(o.CatchUpHours))
            {
                continue;
            }

            if (!await state.TryClaimAsync($"slot|{name}|{StoreJson.Day(today)}|{slotTime:HH\\:mm}", ct))
            {
                continue;
            }

            LogSlot(logger, name, slotTime);
            if (name == "sync")
            {
                await pipeline.RunSyncAsync(ct);
            }
            else if (name == "prices")
            {
                await pipeline.RunPricesAsync(spotOnly: true, ct);
            }
            else
            {
                await reports.EnqueueDailyAsync(ct);
            }
        }

        await heatPump.RunIfDueAsync(ct);
        await dispatcher.DispatchAsync(ct);
    }

    private static TimeOnly ParseTime(string value) => TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Running scheduled {Slot} for {SlotTime}")]
    private static partial void LogSlot(ILogger logger, string slot, TimeOnly slotTime);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduler tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception ex);
}
