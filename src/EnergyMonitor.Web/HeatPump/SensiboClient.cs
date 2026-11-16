using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyMonitor.Core.Model;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Web.HeatPump;

public sealed class SensiboOptions
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://home.sensibo.com/api/v2/";

    /// <summary>From home.sensibo.com/me/api. Key Vault secret <c>Sensibo--ApiKey</c>; never sent to the browser.</summary>
    public string? ApiKey { get; set; }

    /// <summary>How often the heat pump is recorded and its rules evaluated.</summary>
    public int IntervalMinutes { get; set; } = 15;
}

/// <summary>A failed Sensibo call, with a short code that is safe to store, show and send to the browser.</summary>
public sealed class SensiboException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public const string NotConfigured = "sensibo_not_configured";
    public const string Unreachable = "sensibo_unreachable";
    public const string Timeout = "sensibo_timeout";
    public const string Error = "sensibo_error";

    public string Code { get; } = code;

    /// <summary>The code for a non-success Sensibo status.</summary>
    public static string CodeFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Core.Alerts.HeatPumpRules.KeyRejected,
        HttpStatusCode.NotFound => "sensibo_not_found",
        HttpStatusCode.TooManyRequests => "sensibo_rate_limited",
        HttpStatusCode.BadRequest => "sensibo_bad_request",
        _ => Error,
    };
}

/// <summary>
/// Server-side Sensibo calls. Sensibo takes the API key in the query string, so this client writes no
/// request logs, and traces show the URL with its query redacted (the .NET default).
/// </summary>
public sealed class SensiboClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<SensiboOptions> options)
{
    public const string HttpClientName = "sensibo";

    // mainMeasurementsSensor is a paired Room Sensor; when there is one, it is the room temperature (as on the page).
    private const string PodFields = "id,room,acState,measurements,mainMeasurementsSensor,connectionStatus,temperatureUnit";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.CurrentValue.ApiKey);

    public HttpClient CreateClient() => httpClientFactory.CreateClient(HttpClientName);

    /// <summary><paramref name="path"/> and <paramref name="query"/> with the server's key as the only <c>apiKey</c>.</summary>
    public Uri WithKey(string path, IEnumerable<KeyValuePair<string, string?>> query)
    {
        var apiKey = options.CurrentValue.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SensiboException(SensiboException.NotConfigured, "No Sensibo API key is configured.");
        }

        var queryString = QueryString.Create(query
            .Where(pair => !string.Equals(pair.Key, "apiKey", StringComparison.OrdinalIgnoreCase))
            .Append(new KeyValuePair<string, string?>("apiKey", apiKey.Trim())));
        return new Uri(path + queryString.ToUriComponent(), UriKind.Relative);
    }

    /// <summary>Every device the key can see, with its latest measurement and state.</summary>
    public async Task<IReadOnlyList<HeatPumpReading>> DevicesAsync(CancellationToken ct)
    {
        var pods = await GetAsync<Envelope<List<Pod>>>("users/me/pods", [new("fields", PodFields)], ct);
        return [.. (pods?.Result ?? []).Where(p => !string.IsNullOrEmpty(p.Id)).Select(ToReading)];
    }

    /// <summary>The last 24 hours of measurements, which is all Sensibo keeps.</summary>
    public async Task<IReadOnlyList<HeatPumpPoint>> HistoryAsync(string deviceId, CancellationToken ct)
    {
        var history = await GetAsync<Envelope<RawHistory>>($"pods/{Uri.EscapeDataString(deviceId)}/historicalMeasurements", [new("days", "1")], ct);
        var points = new SortedDictionary<long, (double? Temperature, double? Humidity)>();
        foreach (var p in history?.Result?.Temperature ?? [])
        {
            if (p.Value is { } v && ParseTime(p.Time) is { } t)
            {
                var time = t.ToUnixTimeSeconds();
                points[time] = (v, points.GetValueOrDefault(time).Humidity);
            }
        }

        foreach (var p in history?.Result?.Humidity ?? [])
        {
            if (p.Value is { } v && ParseTime(p.Time) is { } t)
            {
                var time = t.ToUnixTimeSeconds();
                points[time] = (points.GetValueOrDefault(time).Temperature, v);
            }
        }

        return [.. points.Select(p => new HeatPumpPoint(p.Key, p.Value.Temperature, p.Value.Humidity))];
    }

    private async Task<T?> GetAsync<T>(string path, KeyValuePair<string, string?>[] query, CancellationToken ct)
    {
        try
        {
            using var response = await CreateClient().GetAsync(WithKey(path, query), ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new SensiboException(SensiboException.CodeFor(response.StatusCode), $"Sensibo answered {(int)response.StatusCode}.");
            }

            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SensiboException(SensiboException.Unreachable, "Sensibo could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SensiboException(SensiboException.Timeout, "Sensibo did not answer in time.", ex);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new SensiboException(SensiboException.Error, "Sensibo sent an unreadable answer.", ex);
        }
    }

    private static HeatPumpReading ToReading(Pod pod)
    {
        var measurements = pod.MainMeasurementsSensor?.Measurements ?? pod.Measurements;
        return new HeatPumpReading(
            pod.Id!,
            pod.Room?.Name is { Length: > 0 } name ? name : pod.Id!,
            measurements?.Temperature,
            measurements?.Humidity,
            ParseTime(measurements?.Time?.Time),
            pod.ConnectionStatus?.IsAlive ?? false,
            ParseTime(pod.ConnectionStatus?.LastSeen?.Time),
            pod.AcState?.On,
            pod.AcState?.Mode,
            pod.AcState?.TargetTemperature,
            pod.AcState?.TemperatureUnit ?? pod.TemperatureUnit);
    }

    // Sensibo sends UTC timestamps, sometimes without an offset.
    private static DateTimeOffset? ParseTime(string? time) =>
        DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;

    private sealed record Envelope<T>(T? Result);

    private sealed record Pod(
        string? Id, Room? Room, AcState? AcState, Measurements? Measurements, Sensor? MainMeasurementsSensor, ConnectionStatus? ConnectionStatus, string? TemperatureUnit);

    private sealed record Sensor(Measurements? Measurements);

    private sealed record Room(string? Name);

    private sealed record AcState(bool? On, string? Mode, double? TargetTemperature, string? TemperatureUnit);

    private sealed record Measurements(double? Temperature, double? Humidity, Moment? Time);

    private sealed record ConnectionStatus(bool? IsAlive, Moment? LastSeen);

    private sealed record Moment(string? Time);

    private sealed record RawPoint(string? Time, double? Value);

    private sealed record RawHistory(List<RawPoint>? Temperature, List<RawPoint>? Humidity);
}
