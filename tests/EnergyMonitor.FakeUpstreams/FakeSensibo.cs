using System.Globalization;
using System.Text.Json.Nodes;

namespace EnergyMonitor.FakeUpstreams;

/// <summary>
/// State of the fake Sensibo API: one heat pump ("Stue") in the summer house. Guarded by
/// <see cref="FakeState.Locked{T}"/> like the rest of the fake.
/// </summary>
public sealed class FakeSensibo
{
    public const string ApiKey = "sensibo-KEY-do-not-log";
    public const string DeviceId = "Pod1Stue";

    public double Temperature { get; set; } = 10.5;

    public double Humidity { get; set; } = 62;

    /// <summary>A Sensibo Room Sensor paired with the pod; Sensibo then reports it as <c>mainMeasurementsSensor</c>.</summary>
    public double? RoomSensorTemperature { get; set; }

    public bool Online { get; set; } = true;

    /// <summary>Every call answers 503.</summary>
    public bool Down { get; set; }

    /// <summary>Every call answers 401, as for a revoked key.</summary>
    public bool KeyRejected { get; set; }

    /// <summary>Every call answers 200 with an HTML page, as a captive portal or a broken proxy would.</summary>
    public bool AnswersHtml { get; set; }

    public JsonObject AcState { get; private set; } = DefaultAcState();

    public JsonObject? Timer { get; set; }

    public bool SmartModeEnabled { get; set; }

    public int PodCalls { get; set; }

    public int HistoryCalls { get; set; }

    /// <summary>The query strings the fake received, so a test can see what the app sent.</summary>
    public List<string> Queries { get; } = [];

    public void Reset()
    {
        Temperature = 10.5;
        Humidity = 62;
        RoomSensorTemperature = null;
        Online = true;
        Down = false;
        KeyRejected = false;
        AnswersHtml = false;
        AcState = DefaultAcState();
        Timer = null;
        SmartModeEnabled = false;
    }

    private static JsonObject DefaultAcState() => new()
    {
        ["on"] = true,
        ["mode"] = "heat",
        ["targetTemperature"] = 10,
        ["temperatureUnit"] = "C",
        ["fanLevel"] = "auto",
        ["swing"] = "stopped",
        ["horizontalSwing"] = "stopped",
        ["light"] = "on",
    };

    public static JsonObject RemoteCapabilities()
    {
        JsonArray Range(int from, int to) => [.. Enumerable.Range(from, to - from + 1).Select(v => (JsonNode)v)];
        JsonObject Mode(int from, int to, bool withTemperatures = true)
        {
            var mode = new JsonObject
            {
                ["fanLevels"] = new JsonArray("quiet", "low", "medium", "high", "auto"),
                ["swing"] = new JsonArray("stopped", "rangeFull"),
                ["horizontalSwing"] = new JsonArray("stopped", "rangeFull"),
                ["light"] = new JsonArray("on", "off"),
            };
            if (withTemperatures)
            {
                mode["temperatures"] = new JsonObject
                {
                    ["C"] = new JsonObject { ["isNative"] = true, ["values"] = Range(from, to) },
                    ["F"] = new JsonObject { ["isNative"] = false, ["values"] = Range((from * 9 / 5) + 32, (to * 9 / 5) + 32) },
                };
            }

            return mode;
        }

        return new JsonObject
        {
            ["modes"] = new JsonObject
            {
                ["heat"] = Mode(10, 30),
                ["cool"] = Mode(16, 30),
                ["dry"] = Mode(16, 30),
                ["fan"] = Mode(0, 0, withTemperatures: false),
                ["auto"] = Mode(16, 30),
            },
        };
    }
}

public static class FakeSensiboApp
{
    public static void MapFakeSensibo(this WebApplication app)
    {
        var sensibo = app.MapGroup("/sensibo/api/v2");

        sensibo.MapGet("/users/me/pods", (HttpRequest request, FakeState state) => state.Locked(() =>
        {
            state.Sensibo.PodCalls++;
            return Guard(request, state) ?? Results.Json(new JsonObject { ["status"] = "success", ["result"] = new JsonArray(Pod(state)) });
        }));

        sensibo.MapGet("/pods/{id}/historicalMeasurements", (string id, HttpRequest request, FakeState state) => state.Locked(() =>
        {
            state.Sensibo.HistoryCalls++;
            return Guard(request, state, id) ?? Results.Json(new JsonObject { ["status"] = "success", ["result"] = History(state) });
        }));

        sensibo.MapPost("/pods/{id}/acStates", async (string id, HttpRequest request, FakeState state) =>
        {
            var body = await JsonNode.ParseAsync(request.Body);
            return state.Locked(() =>
            {
                if (Guard(request, state, id) is { } refused)
                {
                    return refused;
                }

                if (body?["acState"] is not JsonObject requested)
                {
                    return Results.Json(new { status = "error", reason = "missing acState" }, statusCode: 400);
                }

                foreach (var (key, value) in requested)
                {
                    state.Sensibo.AcState[key] = value?.DeepClone();
                }

                return Results.Json(new JsonObject
                {
                    ["status"] = "success",
                    ["result"] = new JsonObject { ["status"] = "Success", ["acState"] = state.Sensibo.AcState.DeepClone() },
                });
            });
        });

        sensibo.MapPut("/pods/{id}/timer/", async (string id, HttpRequest request, FakeState state) =>
        {
            var body = await JsonNode.ParseAsync(request.Body);
            return state.Locked(() =>
            {
                if (Guard(request, state, id) is { } refused)
                {
                    return refused;
                }

                var minutes = body?["minutesFromNow"]?.GetValue<int>() ?? 0;
                state.Sensibo.Timer = new JsonObject
                {
                    ["isEnabled"] = true,
                    ["acState"] = body?["acState"]?.DeepClone(),
                    ["targetTime"] = Iso(state.Now().AddMinutes(minutes)),
                    ["targetTimeSecondsFromNow"] = minutes * 60,
                };
                return Results.Json(new { status = "success" });
            });
        });

        sensibo.MapDelete("/pods/{id}/timer/", (string id, HttpRequest request, FakeState state) => state.Locked(() =>
        {
            if (Guard(request, state, id) is { } refused)
            {
                return refused;
            }

            state.Sensibo.Timer = null;

            // An empty 200, so tests cover a success without a body.
            return Results.Ok();
        }));

        sensibo.MapPut("/pods/{id}/smartmode", async (string id, HttpRequest request, FakeState state) =>
        {
            var body = await JsonNode.ParseAsync(request.Body);
            return state.Locked(() =>
            {
                if (Guard(request, state, id) is { } refused)
                {
                    return refused;
                }

                state.Sensibo.SmartModeEnabled = body?["enabled"]?.GetValue<bool>() ?? false;
                return Results.Json(new { status = "success" });
            });
        });
    }

    private static IResult? Guard(HttpRequest request, FakeState state, string? deviceId = null)
    {
        var fake = state.Sensibo;
        fake.Queries.Add(request.QueryString.Value ?? "");
        if (fake.Down)
        {
            return Results.StatusCode(503);
        }

        if (fake.AnswersHtml)
        {
            return Results.Content("<script>alert(document.cookie)</script>", "text/html");
        }

        if (fake.KeyRejected || request.Query["apiKey"] != FakeSensibo.ApiKey)
        {
            return Results.Json(new { status = "error", reason = "unauthorized" }, statusCode: 401);
        }

        return deviceId is null || deviceId == FakeSensibo.DeviceId ? null : Results.Json(new { status = "error", reason = "not found" }, statusCode: 404);
    }

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static JsonObject Seen(DateTimeOffset time, DateTimeOffset now) =>
        new() { ["time"] = Iso(time), ["secondsAgo"] = (int)(now - time).TotalSeconds };

    private static JsonObject Pod(FakeState state)
    {
        var fake = state.Sensibo;
        var now = state.Now();
        var measuredAt = fake.Online ? now.AddSeconds(-40) : now.AddHours(-3);
        return new JsonObject
        {
            ["id"] = FakeSensibo.DeviceId,
            ["room"] = new JsonObject { ["name"] = "Stue", ["icon"] = "Livingroom" },
            ["acState"] = fake.AcState.DeepClone(),
            ["measurements"] = new JsonObject
            {
                ["temperature"] = fake.Temperature,
                ["humidity"] = fake.Humidity,
                ["feelsLike"] = fake.Temperature,
                ["time"] = Seen(measuredAt, now),
            },
            ["mainMeasurementsSensor"] = fake.RoomSensorTemperature is { } room
                ? new JsonObject
                {
                    ["measurements"] = new JsonObject { ["temperature"] = room, ["humidity"] = fake.Humidity, ["time"] = Seen(measuredAt, now) },
                }
                : null,
            ["remoteCapabilities"] = FakeSensibo.RemoteCapabilities(),
            ["connectionStatus"] = new JsonObject { ["isAlive"] = fake.Online, ["lastSeen"] = Seen(measuredAt, now) },
            ["temperatureUnit"] = "C",
            ["smartMode"] = new JsonObject { ["enabled"] = fake.SmartModeEnabled, ["type"] = "temperature" },
            ["timer"] = fake.Timer?.DeepClone(),
        };
    }

    /// <summary>Sensibo's rolling 24 hours, one reading every 5 minutes, ending at the current reading.</summary>
    private static JsonObject History(FakeState state)
    {
        var fake = state.Sensibo;
        var end = state.Now();
        var last = new DateTimeOffset(end.Year, end.Month, end.Day, end.Hour, end.Minute / 5 * 5, 0, TimeSpan.Zero);
        var temperature = new JsonArray();
        var humidity = new JsonArray();
        for (var time = last.AddHours(-24).AddMinutes(5); time <= last; time = time.AddMinutes(5))
        {
            if (!fake.Online && time > end.AddHours(-3))
            {
                break;
            }

            var wobble = Math.Round(Math.Sin(time.ToUnixTimeSeconds() / 3600.0) * 0.3, 1);
            var current = time == last;
            temperature.Add(new JsonObject { ["time"] = Iso(time), ["value"] = current ? fake.Temperature : Math.Round(fake.Temperature + wobble, 1) });
            humidity.Add(new JsonObject { ["time"] = Iso(time), ["value"] = fake.Humidity });
        }

        return new JsonObject { ["temperature"] = temperature, ["humidity"] = humidity };
    }
}
