using EnergyMonitor.Web.Jobs;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Web.HeatPump;

/// <summary>
/// Every <see cref="SensiboOptions.IntervalMinutes"/>: reads the heat pumps' current state and Sensibo's rolling
/// 24 hours of measurements, records them, and evaluates the heat pump rules. Re-reading the whole 24 hours
/// each time means a gap shorter than a day (restart, outage) is filled in by the next run.
/// </summary>
public sealed partial class HeatPumpCollector(
    SensiboClient sensibo,
    HeatPumpStore store,
    HeatPumpStateService state,
    AlertService alerts,
    OutboxDispatcher dispatcher,
    IOptionsMonitor<SensiboOptions> options,
    TimeProvider time,
    ILogger<HeatPumpCollector> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Runs a collection when a new interval has started since the last one. Called by the scheduler each tick.</summary>
    public async Task RunIfDueAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(o.IntervalMinutes, 1, 24 * 60)).Ticks;
        var now = time.GetUtcNow().UtcTicks;
        if (await state.TryStartSlotAsync(new DateTimeOffset(now - (now % interval), TimeSpan.Zero), ct))
        {
            await CollectAsync(ct);
        }
    }

    /// <summary>One collection. Returns false when it did not run: disabled, no key, or another run in progress.</summary>
    public async Task<bool> CollectAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled || !sensibo.IsConfigured || !await _gate.WaitAsync(0, ct))
        {
            return false;
        }

        try
        {
            var now = time.GetUtcNow();
            var previous = await state.GetStatusAsync(ct);
            HeatPumpStatus status;
            try
            {
                var devices = await sensibo.DevicesAsync(ct);
                var oldest = previous.OldestUtc;
                foreach (var device in devices)
                {
                    var points = await sensibo.HistoryAsync(device.DeviceId, ct);
                    await store.UpsertAsync(device.DeviceId, points, ct);
                    if (points.Count > 0)
                    {
                        var first = DateTimeOffset.FromUnixTimeSeconds(points.Min(p => p.Time));
                        oldest = oldest is null || first < oldest ? first : oldest;
                    }
                }

                status = new HeatPumpStatus(now, now, null, 0, oldest, devices);
                LogCollected(logger, devices.Count);
            }
            catch (SensiboException ex)
            {
                status = previous with { LastAttemptAtUtc = now, LastError = ex.Code, ConsecutiveFailures = previous.ConsecutiveFailures + 1 };
                LogFailed(logger, ex.Code, status.ConsecutiveFailures);
            }

            await state.SetStatusAsync(status, ct);
            await alerts.EvaluateHeatPumpAsync(status, ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a collection, and delivery of what it raised, that outlives the request that asked for it.</summary>
    public void StartInBackground(CancellationToken stopping) => _ = Task.Run(
        async () =>
        {
            try
            {
                await CollectAsync(stopping);
                await dispatcher.DispatchAsync(stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogBackgroundFailed(logger, ex);
            }
        },
        stopping);

    [LoggerMessage(Level = LogLevel.Information, Message = "Heat pump collection finished: {Devices} device(s)")]
    private static partial void LogCollected(ILogger logger, int devices);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heat pump collection failed ({ErrorCode}), {Failures} in a row")]
    private static partial void LogFailed(ILogger logger, string errorCode, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "Manual heat pump collection failed")]
    private static partial void LogBackgroundFailed(ILogger logger, Exception ex);
}
