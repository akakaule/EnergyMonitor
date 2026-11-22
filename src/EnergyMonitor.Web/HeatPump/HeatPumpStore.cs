using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Web.Storage;

namespace EnergyMonitor.Web.HeatPump;

/// <summary>One reading: unix seconds (UTC), °C and % RH. Short JSON names keep an hour's row small.</summary>
public sealed record HeatPumpPoint(
    [property: JsonPropertyName("t")] long Time,
    [property: JsonPropertyName("c")] double? Temperature,
    [property: JsonPropertyName("h")] double? Humidity);

public sealed record HeatPumpHour(IReadOnlyList<HeatPumpPoint> Points);

public sealed record SeriesPoint(DateTimeOffset Time, double Value);

public sealed record HeatPumpHistory(IReadOnlyList<SeriesPoint> Temperature, IReadOnlyList<SeriesPoint> Humidity);

/// <summary>
/// Recorded heat pump readings: partition <c>{device}|{yyyyMM}</c>, row <c>{yyyyMMddHH}</c> (UTC), one row
/// per hour holding that hour's readings (Sensibo gives about 40). A year is about 8,800 rows per device.
/// Re-recording the same readings replaces them, so every collection can re-read Sensibo's whole 24 hours.
/// </summary>
public sealed class HeatPumpStore(ITableBackend tables)
{
    private const string Table = "heatpump";

    /// <summary>Long ranges are averaged into about this many points, so a chart stays light.</summary>
    public const int TargetPoints = 720;

    private static string Pk(string deviceId, DateTime hourUtc) => $"{deviceId}|{hourUtc:yyyyMM}";

    private static string Rk(DateTime hourUtc) => hourUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);

    private static DateTime HourOf(long unixSeconds)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return new DateTime(time.Year, time.Month, time.Day, time.Hour, 0, 0, DateTimeKind.Utc);
    }

    public async Task UpsertAsync(string deviceId, IReadOnlyCollection<HeatPumpPoint> points, CancellationToken ct)
    {
        foreach (var partition in points.GroupBy(p => Pk(deviceId, HourOf(p.Time))))
        {
            var hours = partition.GroupBy(p => HourOf(p.Time)).ToList();
            var from = hours.Min(h => h.Key);
            var to = hours.Max(h => h.Key).AddHours(1);
            var stored = (await tables.QueryAsync(Table, partition.Key, Rk(from), Rk(to), null, ct)).ToDictionary(r => r.Rk);

            var rows = new List<Row>();
            foreach (var hour in hours)
            {
                var merged = stored.TryGetValue(Rk(hour.Key), out var row)
                    ? StoreJson.Read<HeatPumpHour>(row).Points.ToDictionary(p => p.Time)
                    : [];
                foreach (var point in hour)
                {
                    merged[point.Time] = merged.TryGetValue(point.Time, out var old)
                        ? new HeatPumpPoint(point.Time, point.Temperature ?? old.Temperature, point.Humidity ?? old.Humidity)
                        : point;
                }

                rows.Add(new Row(partition.Key, Rk(hour.Key), StoreJson.Write(new HeatPumpHour([.. merged.Values.OrderBy(p => p.Time)]))));
            }

            await tables.UpsertAsync(Table, rows, ct);
        }
    }

    /// <summary>Readings from <paramref name="fromUtc"/> up to <paramref name="toUtc"/>, averaged into time buckets.</summary>
    public async Task<HeatPumpHistory> QueryAsync(string deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var from = fromUtc.ToUnixTimeSeconds();
        var to = toUtc.ToUnixTimeSeconds();
        var bucket = Math.Max(60, (to - from) / TargetPoints);
        var buckets = new SortedDictionary<long, (double T, int Tn, double H, int Hn)>();

        var firstHour = HourOf(from);
        for (var month = new DateTime(firstHour.Year, firstHour.Month, 1, 0, 0, 0, DateTimeKind.Utc); month < toUtc.UtcDateTime; month = month.AddMonths(1))
        {
            var rows = await tables.QueryAsync(Table, Pk(deviceId, month), Rk(firstHour), Rk(HourOf(to).AddHours(1)), null, ct);
            foreach (var point in rows.SelectMany(r => StoreJson.Read<HeatPumpHour>(r).Points).Where(p => p.Time >= from && p.Time < to))
            {
                var key = point.Time / bucket * bucket;
                var sums = buckets.GetValueOrDefault(key);
                if (point.Temperature is { } c)
                {
                    sums.T += c;
                    sums.Tn++;
                }

                if (point.Humidity is { } rh)
                {
                    sums.H += rh;
                    sums.Hn++;
                }

                buckets[key] = sums;
            }
        }

        static SeriesPoint At(long time, double value) => new(DateTimeOffset.FromUnixTimeSeconds(time), Math.Round(value, 2));
        return new HeatPumpHistory(
            [.. buckets.Where(b => b.Value.Tn > 0).Select(b => At(b.Key, b.Value.T / b.Value.Tn))],
            [.. buckets.Where(b => b.Value.Hn > 0).Select(b => At(b.Key, b.Value.H / b.Value.Hn))]);
    }

    /// <summary>
    /// The mean indoor temperature of each local day in [from, to). Hours count equally whatever number of
    /// readings they hold, and a day with readings in fewer than <paramref name="minHours"/> hours has no mean.
    /// </summary>
    public async Task<IReadOnlyDictionary<DateOnly, double>> DailyMeansAsync(
        string deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeZoneInfo zone, int minHours, CancellationToken ct)
    {
        var hours = new List<(DateOnly Day, double C)>();
        var first = HourOf(fromUtc.ToUnixTimeSeconds());
        for (var month = new DateTime(first.Year, first.Month, 1, 0, 0, 0, DateTimeKind.Utc); month < toUtc.UtcDateTime; month = month.AddMonths(1))
        {
            foreach (var row in await tables.QueryAsync(Table, Pk(deviceId, month), Rk(first), Rk(HourOf(toUtc.ToUnixTimeSeconds())), null, ct))
            {
                var readings = StoreJson.Read<HeatPumpHour>(row).Points.Where(p => p.Temperature is not null).ToArray();
                if (readings.Length > 0)
                {
                    var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(readings[0].Time), zone);
                    hours.Add((DateOnly.FromDateTime(local.DateTime), readings.Average(p => p.Temperature!.Value)));
                }
            }
        }

        return hours.GroupBy(h => h.Day).Where(g => g.Count() >= minHours).ToDictionary(g => g.Key, g => g.Average(h => h.C));
    }
}

public sealed record HeatPumpLink(string DeviceId, string PropertyId);

/// <summary>What the last collection found, kept across restarts. <see cref="Devices"/> survive a failed collection.</summary>
public sealed record HeatPumpStatus(
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    string? LastError,
    int ConsecutiveFailures,
    DateTimeOffset? OldestUtc,
    IReadOnlyList<HeatPumpReading> Devices)
{
    public static readonly HeatPumpStatus Empty = new(null, null, null, 0, null, []);
}

/// <summary>Which heat pump belongs to which property, and the collector's status, in the state table.</summary>
public sealed class HeatPumpStateService(IStateStore state)
{
    private const string LinksKey = "settings|heatpump";
    private const string StatusKey = "heatpump|status";
    private const string SlotKey = "heatpump|slot";

    public async Task<IReadOnlyList<HeatPumpLink>> GetLinksAsync(CancellationToken ct) =>
        await state.GetAsync(LinksKey, ct) is { } json ? JsonSerializer.Deserialize<List<HeatPumpLink>>(json, StoreJson.Options) ?? [] : [];

    public Task SetLinksAsync(IReadOnlyList<HeatPumpLink> links, CancellationToken ct) =>
        state.SetAsync(LinksKey, StoreJson.Write(links), ct);

    public async Task<HeatPumpStatus> GetStatusAsync(CancellationToken ct) =>
        await state.GetAsync(StatusKey, ct) is { } json ? JsonSerializer.Deserialize<HeatPumpStatus>(json, StoreJson.Options) ?? HeatPumpStatus.Empty : HeatPumpStatus.Empty;

    public Task SetStatusAsync(HeatPumpStatus status, CancellationToken ct) =>
        state.SetAsync(StatusKey, StoreJson.Write(status), ct);

    /// <summary>The latest reading of each linked property's heat pump, by property id.</summary>
    public async Task<IReadOnlyDictionary<string, HeatPumpReading>> LinkedReadingsAsync(CancellationToken ct)
    {
        var devices = (await GetStatusAsync(ct)).Devices;
        var result = new Dictionary<string, HeatPumpReading>();
        foreach (var link in await GetLinksAsync(ct))
        {
            if (devices.FirstOrDefault(d => d.DeviceId == link.DeviceId) is { } reading)
            {
                result.TryAdd(link.PropertyId, reading);
            }
        }

        return result;
    }

    /// <summary>
    /// Records <paramref name="slotStartUtc"/> as run and returns true, unless it (or a later one) already
    /// was. One scheduler loop and one replica, so a read-then-write is enough, and it leaves one row
    /// instead of one claim per 15 minutes.
    /// </summary>
    public async Task<bool> TryStartSlotAsync(DateTimeOffset slotStartUtc, CancellationToken ct)
    {
        if (await state.GetAsync(SlotKey, ct) is { } json && JsonSerializer.Deserialize<DateTimeOffset>(json, StoreJson.Options) >= slotStartUtc)
        {
            return false;
        }

        await state.SetAsync(SlotKey, StoreJson.Write(slotStartUtc), ct);
        return true;
    }
}
