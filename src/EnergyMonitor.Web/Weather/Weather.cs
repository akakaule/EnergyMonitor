using System.Globalization;
using System.Net;
using System.Text.Json;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;
using EnergyMonitor.Core.Weather;
using EnergyMonitor.Web.HeatPump;
using EnergyMonitor.Web.Storage;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Web.Weather;

public sealed class WeatherOptions
{
    /// <summary>Open-Meteo's forecast API, which also serves the latest 92 days. No key.</summary>
    public string BaseUrl { get; set; } = "https://api.open-meteo.com/";

    /// <summary>How many days back each fetch reaches; Open-Meteo allows at most 92.</summary>
    public int PastDays { get; set; } = 92;

    /// <summary>The indoor temperature assumed for a property without a recorded one.</summary>
    public double BaseTemperatureC { get; set; } = HeatingAnalysis.DefaultBaseC;

    /// <summary>
    /// The consumption alarms judge a day by its outdoor temperature only when the days before it give a
    /// line through at least this many days that explains at least this share (0–1) of their variation.
    /// </summary>
    public int AlarmMinDays { get; set; } = 30;

    public double AlarmMinRSquared { get; set; } = 0.5;

    /// <summary>How many days before the analysed day the line is fitted to.</summary>
    public int AlarmLookbackDays { get; set; } = 90;
}

/// <summary>Hourly outdoor temperature (2 m above ground) at a position, from Open-Meteo.</summary>
public sealed class OpenMeteoClient(IHttpClientFactory factory)
{
    public const string HttpClientName = "weather";

    public async Task<IReadOnlyList<(DateTimeOffset HourUtc, double Celsius)>> FetchAsync(double latitude, double longitude, int pastDays, CancellationToken ct)
    {
        var path = FormattableString.Invariant(
            $"v1/forecast?latitude={latitude:0.####}&longitude={longitude:0.####}&hourly=temperature_2m&past_days={Math.Clamp(pastDays, 1, 92)}&forecast_days=1&timeformat=unixtime");
        HttpResponseMessage response;
        try
        {
            response = await factory.CreateClient(HttpClientName).GetAsync(path, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MeasurementSourceException("weather_unreachable", "The weather source could not be reached.", ex);
        }

        using (response)
        {
            // Open-Meteo answers 429 when the free daily or hourly quota of the calling address is used up.
            if (!response.IsSuccessStatusCode)
            {
                throw new MeasurementSourceException(
                    response.StatusCode == HttpStatusCode.TooManyRequests ? "weather_rate_limited" : "weather_http_" + (int)response.StatusCode,
                    "The weather source failed.");
            }

            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var hourly = json.RootElement.GetProperty("hourly");
                var times = hourly.GetProperty("time");
                var values = hourly.GetProperty("temperature_2m");
                var result = new List<(DateTimeOffset, double)>();
                for (var i = 0; i < times.GetArrayLength() && i < values.GetArrayLength(); i++)
                {
                    // An hour the model has no value for is null; it is left out, never stored as zero.
                    if (values[i].ValueKind == JsonValueKind.Number && Time(times[i]) is { } time)
                    {
                        result.Add((time, values[i].GetDouble()));
                    }
                }

                return result;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new MeasurementSourceException("weather_bad_response", "The weather source answered in an unexpected shape.", ex);
            }
        }
    }

    /// <summary>Unix seconds as asked for; an ISO time without offset (the API's default, GMT) is read too.</summary>
    private static DateTimeOffset? Time(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => DateTimeOffset.FromUnixTimeSeconds(value.GetInt64()),
        JsonValueKind.String when DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) => new DateTimeOffset(time, TimeSpan.Zero),
        _ => null,
    };
}

public sealed record WeatherStatus(DateTimeOffset? LastSuccessUtc, string? ErrorCode);

/// <summary>
/// Stored outdoor temperatures: partition = property, row = UTC day, holding that day's 24 hourly values.
/// A fetch replaces the hours it brings and keeps the rest, so history grows beyond what the source serves.
/// </summary>
public sealed class WeatherStore(ITableBackend tables, IStateStore state)
{
    private const string Table = "weather";

    private sealed record Day(double?[] C);

    private static string Rk(DateTime dayUtc) => dayUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public async Task UpsertAsync(string propertyId, IReadOnlyCollection<(DateTimeOffset HourUtc, double Celsius)> hours, CancellationToken ct)
    {
        if (hours.Count == 0)
        {
            return;
        }

        var from = hours.Min(h => h.HourUtc).UtcDateTime.Date;
        var to = hours.Max(h => h.HourUtc).UtcDateTime.Date.AddDays(1);
        var stored = (await tables.QueryAsync(Table, propertyId, Rk(from), Rk(to), null, ct)).ToDictionary(r => r.Rk);
        var rows = new List<Row>();
        foreach (var day in hours.GroupBy(h => h.HourUtc.UtcDateTime.Date))
        {
            var values = stored.TryGetValue(Rk(day.Key), out var row) && StoreJson.Read<Day>(row).C is { Length: 24 } old ? old : new double?[24];
            foreach (var (hour, celsius) in day)
            {
                values[hour.UtcDateTime.Hour] = Math.Round(celsius, 1);
            }

            rows.Add(new Row(propertyId, Rk(day.Key), StoreJson.Write(new Day(values))));
        }

        await tables.UpsertAsync(Table, rows, ct);
    }

    /// <summary>The stored hourly temperatures in [from, to), by the hour's start.</summary>
    public async Task<IReadOnlyDictionary<DateTimeOffset, double>> GetAsync(string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var result = new Dictionary<DateTimeOffset, double>();
        foreach (var row in await tables.QueryAsync(Table, propertyId, Rk(fromUtc.UtcDateTime.Date), Rk(toUtc.UtcDateTime.Date.AddDays(1)), null, ct))
        {
            var day = new DateTimeOffset(DateTime.ParseExact(row.Rk, "yyyyMMdd", CultureInfo.InvariantCulture), TimeSpan.Zero);
            var values = StoreJson.Read<Day>(row).C;
            for (var hour = 0; hour < values.Length; hour++)
            {
                if (values[hour] is { } c && day.AddHours(hour) is var time && time >= fromUtc && time < toUtc)
                {
                    result[time] = c;
                }
            }
        }

        return result;
    }

    /// <summary>The temperatures of another position must not stay behind when a property is moved.</summary>
    public async Task DeleteAsync(string propertyId, CancellationToken ct)
    {
        await tables.DeleteAsync(Table, await tables.QueryAsync(Table, propertyId, null, null, null, ct), ct);
        await state.DeleteAsync(StatusKey(propertyId), ct);
    }

    public async Task<WeatherStatus> GetStatusAsync(string propertyId, CancellationToken ct) =>
        await state.GetAsync(StatusKey(propertyId), ct) is { } json
            ? JsonSerializer.Deserialize<WeatherStatus>(json, StoreJson.Options) ?? new(null, null)
            : new(null, null);

    public Task SetStatusAsync(string propertyId, WeatherStatus status, CancellationToken ct) =>
        state.SetAsync(StatusKey(propertyId), StoreJson.Write(status), ct);

    private static string StatusKey(string propertyId) => "weather|status|" + propertyId;
}

/// <summary>Fetches the outdoor temperature for every property that has a position. A failure is recorded, never thrown.</summary>
public sealed partial class WeatherSyncService(
    IPropertyStore properties, OpenMeteoClient client, WeatherStore store, IOptions<WeatherOptions> options, TimeProvider time, ILogger<WeatherSyncService> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var property in await properties.ListAsync(ct))
        {
            if (property is not { Latitude: { } latitude, Longitude: { } longitude })
            {
                continue;
            }

            var previous = await store.GetStatusAsync(property.Id, ct);
            try
            {
                var now = time.GetUtcNow();
                // The answer runs to the end of today; hours that have not happened are a forecast and are not kept.
                var hours = (await client.FetchAsync(latitude, longitude, options.Value.PastDays, ct)).Where(h => h.HourUtc.AddHours(1) <= now).ToArray();
                await store.UpsertAsync(property.Id, hours, ct);
                await store.SetStatusAsync(property.Id, new(now, null), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var code = ex is MeasurementSourceException e ? e.ErrorCode : "weather_failed";
                LogFailed(logger, property.Id, code);
                await store.SetStatusAsync(property.Id, previous with { ErrorCode = code }, ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outdoor temperature for {PropertyId} could not be fetched: {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, string propertyId, string errorCode);
}

/// <summary>What the heating analysis of a property returns. <see cref="Status"/> is NoLocation, InsufficientData or Ok.</summary>
public sealed record HeatingView(string PropertyId, string Status, string Unit, int PeriodDays, double BaseTemperatureC, bool UsesIndoor,
    HeatingModel? Model, HeatingDay? Latest, DateTimeOffset? LastWeatherSyncUtc, string? WeatherErrorCode, IReadOnlyList<HeatingDay> Days)
{
    /// <summary>True when the line over this period is good enough for the consumption alarms to judge days by.</summary>
    public bool DependableForAlarms { get; init; }
}

public sealed class HeatingQueryService(
    IPropertyStore properties, IConsumptionStore consumption, WeatherStore weather, HeatPumpStore heatPumps, HeatPumpStateService heatPumpState,
    IOptions<WeatherOptions> options, TimeProvider time)
{
    public const string NoLocation = "NoLocation";
    public const int MinDays = 30;
    public const int MaxDays = 365;
    public const int DefaultDays = 90;

    /// <summary>An indoor day needs readings in at least this many of its hours.</summary>
    private const int MinIndoorHours = 20;

    /// <summary>
    /// Daily consumption against degree-days over the latest <paramref name="days"/> days. Only days with
    /// complete consumption and a temperature for every hour take part.
    /// </summary>
    public async Task<HeatingView> AnalyseAsync(string propertyId, int days, CancellationToken ct)
    {
        if (days is < MinDays or > MaxDays) throw new ArgumentException("invalid_days");
        var property = await properties.GetAsync(propertyId, ct) ?? throw new KeyNotFoundException("property_not_found");
        var baseC = options.Value.BaseTemperatureC;
        if (property.Latitude is null || property.Longitude is null)
        {
            return new(propertyId, NoLocation, "kWh", days, baseC, false, null, null, null, null, []);
        }

        var today = LocalDay.Of(time.GetUtcNow(), LocalDay.Zone(property.TimeZone));
        var input = await InputAsync(property, today.AddDays(-days), today.AddDays(-1), ct);
        var result = HeatingAnalysis.Analyse(input, baseC);
        var status = await weather.GetStatusAsync(propertyId, ct);
        return new(propertyId, result.Status, "kWh", days, baseC, result.UsesIndoor, result.Model, result.Days.LastOrDefault(),
            status.LastSuccessUtc, status.ErrorCode, result.Days)
        {
            DependableForAlarms = result.Model is { } model && HeatingAnalysis.IsDependable(model, options.Value.AlarmMinDays, options.Value.AlarmMinRSquared),
        };
    }

    /// <summary>
    /// What each complete day from <paramref name="from"/> to <paramref name="to"/> should have used for its
    /// outdoor temperature, judged from the days before it. Days without a dependable expectation are left
    /// out, and a property without a position has none.
    /// </summary>
    public async Task<IReadOnlyDictionary<DateOnly, WeatherExpectation>> ExpectationsAsync(Property property, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new Dictionary<DateOnly, WeatherExpectation>();
        if (property.Latitude is null || property.Longitude is null)
        {
            return result;
        }

        var o = options.Value;
        var input = await InputAsync(property, from.AddDays(-o.AlarmLookbackDays), to, ct);
        foreach (var day in input.Where(d => d.Date >= from))
        {
            var window = input.Where(d => d.Date >= day.Date.AddDays(-o.AlarmLookbackDays) && d.Date <= day.Date).ToArray();
            if (HeatingAnalysis.Expect(window, day.Date, o.BaseTemperatureC, o.AlarmMinDays, o.AlarmMinRSquared) is { } expectation)
            {
                result[day.Date] = expectation;
            }
        }

        return result;
    }

    /// <summary>The days from <paramref name="from"/> to <paramref name="to"/> with complete consumption and a temperature for every hour.</summary>
    private async Task<List<(DateOnly Date, double KWh, double OutdoorC, double? IndoorC)>> InputAsync(Property property, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var propertyId = property.Id;
        var zone = LocalDay.Zone(property.TimeZone);
        var (startUtc, endUtc) = (LocalDay.StartUtc(from, zone), LocalDay.StartUtc(to.AddDays(1), zone));
        var outdoor = await weather.GetAsync(propertyId, startUtc, endUtc, ct);
        var indoor = (await heatPumpState.GetLinksAsync(ct)).FirstOrDefault(l => l.PropertyId == propertyId) is { } link
            ? await heatPumps.DailyMeansAsync(link.DeviceId, startUtc, endUtc, zone, MinIndoorHours, ct)
            : new Dictionary<DateOnly, double>();

        var input = new List<(DateOnly, double, double, double?)>();
        foreach (var day in (await consumption.GetDailyAsync(propertyId, from, to, ct)).Where(d => d.IsComplete && d.KWh is not null))
        {
            var (dayStart, dayEnd) = LocalDay.Bounds(day.LocalDate, zone);
            var temperatures = new List<double>();
            for (var hour = dayStart; hour < dayEnd; hour = hour.AddHours(1))
            {
                if (outdoor.TryGetValue(hour, out var c)) temperatures.Add(c);
            }

            // A mean over part of a day would lean towards the night or the afternoon.
            if (temperatures.Count == (int)(dayEnd - dayStart).TotalHours)
            {
                input.Add((day.LocalDate, day.KWh!.Value, temperatures.Average(), indoor.TryGetValue(day.LocalDate, out var inside) ? inside : null));
            }
        }

        return input;
    }
}
