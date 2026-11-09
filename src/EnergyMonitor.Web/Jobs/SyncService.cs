using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Aggregation;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;
using EnergyMonitor.Web.Eloverblik;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Web.Jobs;

public sealed class SyncOptions
{
    /// <summary>History fetched the first time a property is synchronised.</summary>
    public int BackfillDays { get; set; } = 90;

    /// <summary>Days fetched again on every run, to pick up late data and corrections.</summary>
    public int RefetchDays { get; set; } = 7;

    public int ChunkDays { get; set; } = 30;
}

/// <summary>
/// Pulls intervals from the measurement source, upserts them and recomputes the affected local days.
/// Safe to repeat: storing the same interval twice changes nothing, and a corrected value replaces the old one.
/// </summary>
public sealed partial class SyncService(
    IPropertyStore properties,
    IConsumptionStore consumption,
    ISyncRunStore runs,
    IStateStore state,
    IMeasurementSource source,
    TimeProvider time,
    IOptions<SyncOptions> options,
    ILogger<SyncService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _gate.CurrentCount == 0;

    public static string BackfillKey(string propertyId) => $"backfill|{propertyId}";

    /// <summary>Runs one sync. Returns null when another run is already in progress.</summary>
    public async Task<SyncRun?> RunAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            return null;
        }

        try
        {
            var run = new SyncRun(Guid.NewGuid().ToString("N"), source.SourceId, time.GetUtcNow(), null, SyncStatus.Running, 0, null);
            await runs.SaveAsync(run, ct);

            var count = 0;
            string? error = null;
            foreach (var property in await properties.ListAsync(ct))
            {
                if (string.IsNullOrWhiteSpace(property.MeteringPointId))
                {
                    continue;
                }

                try
                {
                    count += await SyncPropertyAsync(property, ct);
                }
                catch (MeasurementSourceException ex)
                {
                    error ??= ex.ErrorCode;
                    LogSourceError(logger, property.Name, EloverblikClient.Mask(property.MeteringPointId), ex.ErrorCode);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error ??= "unexpected";
                    LogUnexpected(logger, ex, property.Name);
                }
            }

            run = run with
            {
                FinishedAtUtc = time.GetUtcNow(),
                Status = error is null ? SyncStatus.Succeeded : SyncStatus.Failed,
                IntervalCount = count,
                ErrorCode = error,
            };
            await runs.SaveAsync(run, ct);
            LogFinished(logger, run.Status, count, error);
            return run;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> SyncPropertyAsync(Property property, CancellationToken ct)
    {
        var o = options.Value;
        var zone = LocalDay.Zone(property.TimeZone);
        var now = time.GetUtcNow();
        var today = LocalDay.Of(now, zone);
        var backfilled = await state.GetAsync(BackfillKey(property.Id), ct) is not null;
        var from = today.AddDays(-(backfilled ? o.RefetchDays : o.BackfillDays));

        var count = 0;
        for (var chunkFrom = from; chunkFrom < today; chunkFrom = chunkFrom.AddDays(o.ChunkDays))
        {
            var chunkTo = chunkFrom.AddDays(o.ChunkDays) < today ? chunkFrom.AddDays(o.ChunkDays) : today;
            var measurements = await source.FetchAsync(property, chunkFrom, chunkTo, ct);
            var intervals = measurements
                .Where(m => m.Metric == Measurement.EnergyConsumption && m.IntervalEndUtc is not null && m.Resolution is not null)
                .Select(m => new ConsumptionInterval(
                    property.Id, m.SourceId, m.ObservedAtUtc, m.IntervalEndUtc!.Value, m.Value, m.Resolution!, m.Quality, now))
                .ToList();
            await consumption.UpsertIntervalsAsync(intervals, ct);
            count += intervals.Count;
        }

        // Every ended day in the window gets a row, including days the source had nothing for:
        // those are stored as missing, with no sum.
        var lastEnded = today.AddDays(-1);
        if (from <= lastEnded)
        {
            var stored = await consumption.GetIntervalsAsync(
                property.Id, source.SourceId, LocalDay.StartUtc(from, zone), LocalDay.StartUtc(today, zone), ct);
            var byDay = stored.ToLookup(i => LocalDay.Of(i.StartUtc, zone));
            var days = LocalDay.Range(from, lastEnded)
                .Select(d => DailyAggregator.Aggregate(property.Id, d, zone, byDay[d], now))
                .ToList();
            await consumption.UpsertDailyAsync(days, ct);
        }

        if (!backfilled)
        {
            await state.SetAsync(BackfillKey(property.Id), StoreJsonDate(now), ct);
        }

        return count;
    }

    private static string StoreJsonDate(DateTimeOffset value) => $"\"{value:O}\"";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync failed for {Property} (metering point {MeteringPoint}): {ErrorCode}")]
    private static partial void LogSourceError(ILogger logger, string property, string meteringPoint, string errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected sync failure for {Property}")]
    private static partial void LogUnexpected(ILogger logger, Exception ex, string property);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sync finished: {Status}, {IntervalCount} intervals, error {ErrorCode}")]
    private static partial void LogFinished(ILogger logger, SyncStatus status, int intervalCount, string? errorCode);
}
