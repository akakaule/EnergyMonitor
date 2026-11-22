using System.Globalization;
using System.Text.Json.Nodes;

namespace EnergyMonitor.FakeUpstreams;

/// <summary>
/// Controllable state of the fake Eloverblik and Telegram services. Tests set it directly; a local run
/// sets it through the <c>/control</c> endpoints.
/// </summary>
public sealed class FakeState
{
    public const string Hjem = "571313174000000011";
    public const string Sommerhus = "571313174000000028";

    private readonly Lock _gate = new();
    private int _tokenCounter;

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    /// <summary>Days from today back to the newest day with data. 2 means yesterday is not available yet.</summary>
    public int DelayDays { get; set; } = 2;

    public bool RefreshTokenRejected { get; set; }

    /// <summary>Status returned instead of data (429 or 503) while <see cref="FailuresLeft"/> is above zero.</summary>
    public int FailStatus { get; set; } = 503;

    public int FailuresLeft { get; set; }

    public bool TelegramDown { get; set; }

    /// <summary>Chats that answer 403, as when a recipient has blocked the bot.</summary>
    public HashSet<string> TelegramBlockedChats { get; } = [];

    /// <summary>Chats that answer 500, a failure that may pass.</summary>
    public HashSet<string> TelegramFailingChats { get; } = [];

    public Dictionary<string, double> BaseKWhPerDay { get; } = new() { [Hjem] = 15.8, [Sommerhus] = 7.4 };

    /// <summary>Multiplier for one day of one metering point, e.g. 0.15 for an unusually low day.</summary>
    public Dictionary<(string MeteringPoint, DateOnly Date), double> DayFactors { get; } = [];

    /// <summary>Only this many hours exist for the day.</summary>
    public Dictionary<(string MeteringPoint, DateOnly Date), int> PartialDays { get; } = [];

    /// <summary>No data on or after this date.</summary>
    public Dictionary<string, DateOnly> MissingFrom { get; } = [];

    /// <summary>Metering points that only have daily values: hourly requests fail with error 30008.</summary>
    public HashSet<string> DailyOnly { get; } = [];

    public string? CurrentAccessToken { get; private set; }

    public int TokenCalls { get; private set; }

    public int TimeSeriesCalls { get; private set; }

    public List<(string ChatId, string Text)> TelegramMessages { get; } = [];

    public FakeSensibo Sensibo { get; } = new();

    /// <summary>Local hour from which tomorrow's day-ahead prices are published. Later days never are.</summary>
    public int SpotPublishHour { get; set; } = 13;

    /// <summary>Tomorrow's day-ahead prices stay unpublished.</summary>
    public bool SpotTomorrowWithheld { get; set; }

    /// <summary>Scales a day's day-ahead prices; 1 when the day is not listed.</summary>
    public Dictionary<DateOnly, double> SpotDayFactors { get; } = [];

    /// <summary>Days whose prices are negative from 02:00 to 04:00.</summary>
    public HashSet<DateOnly> SpotNegativeDays { get; } = [];

    public int SpotCalls { get; set; }

    /// <summary>The weather service answers 429, as Open-Meteo does when the free quota is used up.</summary>
    public bool WeatherDown { get; set; }

    public int WeatherCalls { get; set; }

    /// <summary>Days with another outdoor temperature (°C, the day's mean) than the fake's slow wave.</summary>
    public Dictionary<DateOnly, double> OutdoorOverrides { get; } = [];

    /// <summary>What a degree-day costs the summer house in the "heating" scenario, on top of 2 kWh a day.</summary>
    public const double HeatingKWhPerDegreeDay = 0.55;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now(), Zone).DateTime);

    public DateOnly LatestAvailable => Today.AddDays(-DelayDays);

    public T Locked<T>(Func<T> action)
    {
        lock (_gate)
        {
            return action();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            DelayDays = 2;
            RefreshTokenRejected = false;
            FailuresLeft = 0;
            TelegramDown = false;
            DayFactors.Clear();
            PartialDays.Clear();
            MissingFrom.Clear();
            DailyOnly.Clear();
            SpotTomorrowWithheld = false;
            SpotDayFactors.Clear();
            SpotNegativeDays.Clear();
            WeatherDown = false;
            OutdoorOverrides.Clear();
            Sensibo.Reset();
        }
    }

    public string IssueAccessToken()
    {
        TokenCalls++;
        return CurrentAccessToken = $"fake-access-{++_tokenCounter}";
    }

    public void InvalidateAccessToken() => CurrentAccessToken = null;

    public void CountTimeSeriesCall() => TimeSeriesCalls++;

    public bool ApplyScenario(string name)
    {
        lock (_gate)
        {
            switch (name)
            {
                case "normal":
                    Reset();
                    return true;
                case "low":
                    DayFactors[(Sommerhus, LatestAvailable)] = 0.15;
                    return true;
                case "cold-snap":
                    // The latest day was 25 degrees below zero: cold enough that what it costs is more than twice
                    // the median of the days before. Run "heating" after it, so the consumption follows.
                    OutdoorOverrides[LatestAvailable] = -25;
                    return true;
                case "low2":
                    DayFactors[(Sommerhus, LatestAvailable)] = 0.15;
                    DayFactors[(Sommerhus, LatestAvailable.AddDays(-1))] = 0.15;
                    return true;
                case "high":
                    DayFactors[(Hjem, LatestAvailable)] = 2.6;
                    return true;
                case "missing":
                    MissingFrom[Sommerhus] = Today.AddDays(-5);
                    return true;
                case "partial":
                    PartialDays[(Sommerhus, LatestAvailable)] = 9;
                    return true;
                case "outage":
                    FailStatus = 503;
                    FailuresLeft = int.MaxValue;
                    return true;
                case "token-expired":
                    RefreshTokenRejected = true;
                    CurrentAccessToken = null;
                    return true;
                case "telegram-down":
                    TelegramDown = true;
                    return true;
                case "telegram-up":
                    TelegramDown = false;
                    return true;
                case "heating":
                    // The summer house's consumption follows the fake outdoor temperature: 2 kWh a day plus
                    // 0,55 kWh per degree below 17 °C, with the usual few percent of day-to-day variation.
                    for (var date = Today.AddDays(-130); date <= Today; date = date.AddDays(1))
                    {
                        DayFactors[(Sommerhus, date)] = (2 + (HeatingKWhPerDegreeDay * Math.Max(0, 17 - OutdoorDayMean(date)))) / BaseKWhPerDay[Sommerhus];
                    }

                    return true;
                case "weather-down":
                    WeatherDown = true;
                    return true;
                case "cold":
                    Sensibo.Temperature = 3.5;
                    return true;
                case "offline":
                    Sensibo.Online = false;
                    return true;
                case "sensibo-down":
                    Sensibo.Down = true;
                    return true;
                case "sensibo-key-rejected":
                    Sensibo.KeyRejected = true;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>The fake outdoor temperature of a day, °C: between 1 and 11, changing slowly from day to day.</summary>
    public double OutdoorDayMean(DateOnly date) => OutdoorOverrides.TryGetValue(date, out var c) ? c : 6 + (5 * Math.Sin(date.DayNumber / 9.0));

    /// <summary>The day's mean with an afternoon two degrees warmer and a night two degrees colder.</summary>
    public double OutdoorC(DateTimeOffset hourUtc)
    {
        var local = TimeZoneInfo.ConvertTime(hourUtc, Zone);
        return Math.Round(OutdoorDayMean(DateOnly.FromDateTime(local.DateTime)) + (2 * Math.Sin((local.Hour - 9) / 24.0 * 2 * Math.PI)), 1);
    }

    /// <summary>
    /// Day-ahead price in DKK per MWh: the same curve every day, cheapest at 07 and dearest at 19, so two
    /// days differ only by their factor.
    /// </summary>
    public double SpotPrice(DateOnly date, DateTimeOffset startUtc)
    {
        var localHour = TimeZoneInfo.ConvertTime(startUtc, Zone).Hour;
        if (SpotNegativeDays.Contains(date) && localHour is 2 or 3)
        {
            return -50;
        }

        return Math.Round((600 + (250 * Math.Sin((localHour - 13) / 24.0 * 2 * Math.PI))) * SpotDayFactors.GetValueOrDefault(date, 1), 2);
    }

    /// <summary>Deterministic hourly value: a daily shape with a little stable variation.</summary>
    public double HourValue(string meteringPoint, DateOnly date, DateTimeOffset hourStartUtc)
    {
        var perDay = BaseKWhPerDay.GetValueOrDefault(meteringPoint, 10) * DayFactors.GetValueOrDefault((meteringPoint, date), 1);
        var localHour = TimeZoneInfo.ConvertTime(hourStartUtc, Zone).Hour;
        var shape = 0.7 + (0.6 * Math.Sin((localHour - 6) / 24.0 * 2 * Math.PI) * 0.5) + 0.3;
        var jitter = 0.95 + ((((date.DayNumber * 31) + meteringPoint[^1]) % 11) / 100.0);
        return Math.Round(perDay / 24 * shape * jitter, 3);
    }
}

public static class FakeUpstreamsApp
{
    public static void MapFakeUpstreams(this WebApplication app)
    {
        var eloverblik = app.MapGroup("/customerapi/api");

        eloverblik.MapGet("/token", (HttpRequest request, FakeState state) => state.Locked(() =>
        {
            if (state.FailuresLeft > 0)
            {
                state.FailuresLeft--;
                return Results.StatusCode(state.FailStatus);
            }

            return Bearer(request) is null || state.RefreshTokenRejected
                ? Results.Unauthorized()
                : Results.Json(new { result = state.IssueAccessToken() });
        }));

        eloverblik.MapGet("/meteringpoints/meteringpoints", (HttpRequest request, FakeState state) => state.Locked(() =>
            Guard(request, state) ?? Results.Json(new
            {
                result = new[]
                {
                    MeteringPoint(FakeState.Hjem, "Eksempelvej", "12", "2100", "København Ø"),
                    MeteringPoint(FakeState.Sommerhus, "Klitvejen", "3", "4573", "Højby"),
                },
            })));

        eloverblik.MapPost("/meterdata/gettimeseries/{dateFrom}/{dateTo}/{aggregation}", async (
            string dateFrom, string dateTo, string aggregation, HttpRequest request, FakeState state) =>
        {
            var body = await JsonNode.ParseAsync(request.Body);
            var ids = body?["meteringPoints"]?["meteringPoint"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
            return state.Locked(() =>
            {
                state.CountTimeSeriesCall();
                if (Guard(request, state) is { } refused)
                {
                    return refused;
                }

                if (!DateOnly.TryParseExact(dateFrom, "yyyy-MM-dd", out var from) || !DateOnly.TryParseExact(dateTo, "yyyy-MM-dd", out var to))
                {
                    return Results.Json(new { errorCode = 30004, errorText = "InvalidDateFormat" }, statusCode: 400);
                }

                if (from >= to)
                {
                    return Results.Json(new { errorCode = from == to ? 30002 : 30001 }, statusCode: 400);
                }

                if (aggregation == "Hour" && ids.Any(state.DailyOnly.Contains))
                {
                    return Results.Json(new { errorCode = 30008, errorText = "AggregationNotSupported" }, statusCode: 400);
                }

                return Results.Json(new { result = ids.Select(id => TimeSeries(state, id, from, to, aggregation)).ToArray() });
            });
        });

        // Telegram Bot API: POST /bot<token>/sendMessage
        app.MapPost("/{bot}/sendMessage", async (string bot, HttpRequest request, FakeState state) =>
        {
            var body = await JsonNode.ParseAsync(request.Body);
            return state.Locked(() =>
            {
                if (!bot.StartsWith("bot", StringComparison.Ordinal) || bot.Length <= 3)
                {
                    return Results.NotFound();
                }

                var chatId = body?["chat_id"]?.ToString() ?? "";
                if (state.TelegramDown || state.TelegramFailingChats.Contains(chatId))
                {
                    return Results.Json(new { ok = false, description = "Internal Server Error" }, statusCode: 500);
                }

                if (state.TelegramBlockedChats.Contains(chatId))
                {
                    return Results.Json(new { ok = false, description = "Forbidden: bot was blocked by the user" }, statusCode: 403);
                }

                state.TelegramMessages.Add((chatId, body?["text"]?.ToString() ?? ""));
                return Results.Json(new { ok = true });
            });
        });

        // Energi Data Service: quarter-hour day-ahead prices. start and end are Danish dates, end exclusive.
        app.MapGet("/dataset/DayAheadPrices", (string start, string end, string? filter, FakeState state) => state.Locked(() =>
        {
            state.SpotCalls++;
            if (!DateOnly.TryParseExact(start, "yyyy-MM-dd", out var from) || !DateOnly.TryParseExact(end, "yyyy-MM-dd", out var to))
            {
                return Results.BadRequest();
            }

            var areas = filter is null ? ["DK1", "DK2"]
                : JsonNode.Parse(filter)?["PriceArea"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
            var local = TimeZoneInfo.ConvertTime(state.Now(), state.Zone);
            var published = state.Today.AddDays(local.Hour >= state.SpotPublishHour && !state.SpotTomorrowWithheld ? 1 : 0);
            var records = new List<object>();
            for (var date = from; date < to && date <= published; date = date.AddDays(1))
            {
                var dayStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), state.Zone), TimeSpan.Zero);
                var dayEnd = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), state.Zone), TimeSpan.Zero);
                for (var t = dayStart; t < dayEnd; t = t.AddMinutes(15))
                {
                    foreach (var area in areas)
                    {
                        records.Add(new { TimeUTC = t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), PriceArea = area, DayAheadPriceDKK = state.SpotPrice(date, t) });
                    }
                }
            }

            return Results.Json(new { total = records.Count, records }, new System.Text.Json.JsonSerializerOptions());
        }));

        // Open-Meteo: hourly temperature from past_days back to the end of today (UTC), times as unix seconds.
        app.MapGet("/v1/forecast", (double latitude, double longitude, int? past_days, FakeState state) => state.Locked(() =>
        {
            state.WeatherCalls++;
            if (state.WeatherDown)
            {
                return Results.Json(new { error = true, reason = "Daily API request limit exceeded. Please try again tomorrow." }, statusCode: 429);
            }

            var today = new DateTimeOffset(state.Now().UtcDateTime.Date, TimeSpan.Zero);
            var times = new List<long>();
            var temperatures = new List<double>();
            for (var t = today.AddDays(-(past_days ?? 0)); t < today.AddDays(1); t = t.AddHours(1))
            {
                times.Add(t.ToUnixTimeSeconds());
                temperatures.Add(state.OutdoorC(t));
            }

            return Results.Json(new
            {
                latitude, longitude, utc_offset_seconds = 0, timezone = "GMT",
                hourly_units = new { time = "unixtime", temperature_2m = "°C" },
                hourly = new { time = times, temperature_2m = temperatures },
            }, new System.Text.Json.JsonSerializerOptions());
        }));

        app.MapFakeSensibo();

        app.MapPost("/control/scenario/{name}", (string name, FakeState state) =>
            state.ApplyScenario(name) ? Results.Ok(new { applied = name }) : Results.NotFound());

        app.MapGet("/control/telegram", (FakeState state) => state.Locked(() =>
            Results.Json(state.TelegramMessages.Select(m => new { chatId = m.ChatId, text = m.Text }).ToArray())));

        app.MapGet("/", () => "Fake Eloverblik + Telegram + Sensibo. POST /control/scenario/{normal|low|cold-snap|low2|high|missing|partial|outage|token-expired|telegram-down|telegram-up|heating|weather-down|cold|offline|sensibo-down|sensibo-key-rejected}, GET /control/telegram");
    }

    private static string? Bearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) && header.Length > 7 ? header[7..] : null;
    }

    private static IResult? Guard(HttpRequest request, FakeState state)
    {
        if (state.FailuresLeft > 0)
        {
            state.FailuresLeft--;
            return Results.StatusCode(state.FailStatus);
        }

        return Bearer(request) is { } token && token == state.CurrentAccessToken ? null : Results.Unauthorized();
    }

    private static object MeteringPoint(string id, string street, string number, string postcode, string city) => new
    {
        meteringPointId = id,
        typeOfMP = "E17",
        streetName = street,
        buildingNumber = number,
        postcode,
        cityName = city,
        settlementMethod = "E02",
        meterReadingOccurrence = "PT1H",
        hasRelation = true,
    };

    private static JsonObject TimeSeries(FakeState state, string id, DateOnly from, DateOnly to, string aggregation)
    {
        if (!state.BaseKWhPerDay.ContainsKey(id))
        {
            return new JsonObject { ["success"] = false, ["errorCode"] = 20008, ["errorText"] = "MeteringPointNotFound", ["id"] = id };
        }

        var periods = new JsonArray();
        for (var date = from; date < to; date = date.AddDays(1))
        {
            if (date > state.LatestAvailable || (state.MissingFrom.TryGetValue(id, out var missingFrom) && date >= missingFrom))
            {
                continue;
            }

            var start = StartUtc(date, state.Zone);
            var end = StartUtc(date.AddDays(1), state.Zone);
            var hours = (int)Math.Round((end - start).TotalHours);
            var available = Math.Min(hours, state.PartialDays.GetValueOrDefault((id, date), hours));
            var points = new JsonArray();
            if (aggregation == "Day")
            {
                if (available < hours)
                {
                    continue;
                }

                var sum = Enumerable.Range(0, hours).Sum(h => state.HourValue(id, date, start.AddHours(h)));
                points.Add(Point(1, sum));
            }
            else
            {
                for (var h = 0; h < available; h++)
                {
                    points.Add(Point(h + 1, state.HourValue(id, date, start.AddHours(h))));
                }
            }

            periods.Add(new JsonObject
            {
                ["resolution"] = aggregation == "Day" ? "P1D" : "PT1H",
                ["timeInterval"] = new JsonObject { ["start"] = Iso(start), ["end"] = Iso(end) },
                ["Point"] = points,
            });
        }

        if (periods.Count == 0)
        {
            return new JsonObject { ["success"] = false, ["errorCode"] = 30015, ["errorText"] = "NoDataAvailable", ["id"] = id };
        }

        return new JsonObject
        {
            ["success"] = true,
            ["errorCode"] = 10000,
            ["errorText"] = "NoError",
            ["id"] = id,
            ["MyEnergyData_MarketDocument"] = new JsonObject
            {
                ["mRID"] = Guid.NewGuid().ToString(),
                ["sender_MarketParticipant.name"] = "Energinet",
                ["TimeSeries"] = new JsonArray(new JsonObject
                {
                    ["mRID"] = id,
                    ["businessType"] = "A04",
                    ["curveType"] = "A01",
                    ["measurement_Unit.name"] = "KWH",
                    ["Period"] = periods,
                }),
            },
        };
    }

    private static JsonObject Point(int position, double quantity) => new()
    {
        ["position"] = position.ToString(CultureInfo.InvariantCulture),
        ["out_Quantity.quantity"] = quantity.ToString("0.###", CultureInfo.InvariantCulture),
        ["out_Quantity.quality"] = "A04",
    };

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset StartUtc(DateOnly date, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
}

/// <summary>Marker for hosting the fake in tests.</summary>
public sealed class FakeUpstreamsMarker;
