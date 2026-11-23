using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Web.Auth;
using EnergyMonitor.Web.Storage;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace EnergyMonitor.Web.HeatPump;

public sealed record HeatPumpLinkUpdate(string? DeviceId, string? PropertyId);

public sealed record HeatPumpLinksUpdate(IReadOnlyList<HeatPumpLinkUpdate>? Links);

/// <summary>
/// The heat pump module (Sensibo), on when <c>Sensibo:Enabled</c>. The browser never sees the API key:
/// it calls a small allowlist of Sensibo endpoints through <c>/api/heatpump/sensibo</c>, and the server adds the key.
/// </summary>
public static partial class HeatPumpEndpoints
{
    private const int MaxDays = 366;

    // Only what the UI uses; anything else (schedules, Airbend, encoded traversal) is refused before Sensibo is called.
    [GeneratedRegex(@"^(users/me/pods|pods/[A-Za-z0-9]+(/(acStates(/[A-Za-z]+)?|historicalMeasurements|smartmode|timer/?))?)\z")]
    private static partial Regex AllowedPath();

    [GeneratedRegex("^[A-Za-z0-9]{1,64}$")]
    private static partial Regex DeviceIdRegex();

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue<bool>("Sensibo:Enabled");

    /// <summary>Proxy requests per user and minute. The page needs a few per minute; this is far above that.</summary>
    public const int ProxyRequestsPerMinute = 60;

    private const string ProxyRateLimit = "sensibo-proxy";

    public static void AddHeatPump(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<SensiboOptions>(builder.Configuration.GetSection("Sensibo"));
        builder.Services.AddHttpClient(SensiboClient.HttpClientName, (services, client) =>
            {
                var baseUrl = services.GetRequiredService<IOptionsMonitor<SensiboOptions>>().CurrentValue.BaseUrl;
                client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            // Sensibo grants a higher rate limit to gzip clients.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
            // The request URL carries the API key.
            .RemoveAllLoggers();
        builder.Services.AddSingleton<SensiboClient>();
        builder.Services.AddSingleton<HeatPumpStore>();
        builder.Services.AddSingleton<HeatPumpStateService>();
        builder.Services.AddSingleton<HeatPumpCollector>();

        // The proxy shares the API key, and so Sensibo's rate limit, with the collector. Without a limit one
        // signed-in user could use it up, and the frost monitoring would run on stale readings.
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(ProxyRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst("oid")?.Value ?? context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = ProxyRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    public static void MapHeatPump(this WebApplication app)
    {
        if (!IsEnabled(app.Configuration))
        {
            return;
        }

        var api = app.MapGroup("/api/heatpump").RequireAuthorization(AuthSetup.Viewer);
        var admin = app.MapGroup("/api/admin/heatpump").RequireAuthorization(AuthSetup.Admin);

        api.MapGet("", async (HeatPumpStateService state, SensiboClient sensibo, IOptionsMonitor<SensiboOptions> options, CancellationToken ct) =>
        {
            var status = await state.GetStatusAsync(ct);
            var links = await state.GetLinksAsync(ct);
            return Results.Ok(new
            {
                configured = sensibo.IsConfigured,
                lastSuccessAtUtc = status.LastSuccessAtUtc,
                lastAttemptAtUtc = status.LastAttemptAtUtc,
                lastError = status.LastError,
                oldestUtc = status.OldestUtc,
                intervalMinutes = options.CurrentValue.IntervalMinutes,
                devices = status.Devices.Select(d => new
                {
                    id = d.DeviceId,
                    name = d.Name,
                    propertyId = links.FirstOrDefault(l => l.DeviceId == d.DeviceId)?.PropertyId,
                    temperature = d.TemperatureC,
                    humidity = d.Humidity,
                    measuredAtUtc = d.MeasuredAtUtc,
                    online = d.Online,
                    lastSeenUtc = d.LastSeenUtc,
                    on = d.On,
                    mode = d.Mode,
                    targetTemperature = d.TargetTemperature,
                    temperatureUnit = d.TemperatureUnit,
                }),
            });
        });

        api.MapGet("/history/{deviceId}", async (string deviceId, int? days, HeatPumpStateService state, HeatPumpStore store, TimeProvider time, CancellationToken ct) =>
        {
            if (days is < 1 or > MaxDays)
            {
                return Results.BadRequest(new { error = "invalid_days" });
            }

            if (!(await state.GetStatusAsync(ct)).Devices.Any(d => d.DeviceId == deviceId))
            {
                return Results.NotFound();
            }

            var now = time.GetUtcNow();
            return Results.Ok(await store.QueryAsync(deviceId, now.AddDays(-(days ?? 1)), now.AddMinutes(1), ct));
        });

        admin.MapPut("/links", async (HeatPumpLinksUpdate update, HeatPumpStateService state, IPropertyStore properties, CancellationToken ct) =>
        {
            var links = new List<HeatPumpLink>();
            foreach (var link in update.Links ?? [])
            {
                if (link.DeviceId is null || !DeviceIdRegex().IsMatch(link.DeviceId) || links.Any(l => l.DeviceId == link.DeviceId))
                {
                    return Results.BadRequest(new { error = "invalid_device" });
                }

                if (string.IsNullOrEmpty(link.PropertyId))
                {
                    continue;
                }

                if (await properties.GetAsync(link.PropertyId, ct) is null)
                {
                    return Results.BadRequest(new { error = "unknown_property" });
                }

                // The rules judge one pump per house; two would open and close each other's alarms.
                if (links.Any(l => l.PropertyId == link.PropertyId))
                {
                    return Results.BadRequest(new { error = "property_taken" });
                }

                links.Add(new HeatPumpLink(link.DeviceId, link.PropertyId));
            }

            await state.SetLinksAsync(links, ct);
            return Results.NoContent();
        });

        admin.MapPost("/collect", (HeatPumpCollector collector, IHostApplicationLifetime lifetime) =>
        {
            if (collector.IsRunning)
            {
                return Results.Conflict(new { error = "collect_running" });
            }

            collector.StartInBackground(lifetime.ApplicationStopping);
            return Results.Accepted();
        });

        var proxy = app.MapGroup("/api/heatpump/sensibo").RequireAuthorization(AuthSetup.Viewer).RequireRateLimiting(ProxyRateLimit);
        proxy.MapGet("/{**path}", ForwardAsync);
        proxy.MapMethods("/{**path}", ["POST", "PUT", "PATCH", "DELETE"], ForwardAsync).RequireAuthorization(AuthSetup.Admin);
    }

    private static async Task ForwardAsync(HttpContext context, string path, SensiboClient sensibo)
    {
        var request = context.Request;
        var response = context.Response;
        if (!AllowedPath().IsMatch(path))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!sensibo.IsConfigured)
        {
            await Fail(response, StatusCodes.Status503ServiceUnavailable, SensiboException.NotConfigured);
            return;
        }

        var query = request.Query.SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)));
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.Method), sensibo.WithKey(path, query));
        if (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsPatch(request.Method))
        {
            // Only JSON is forwarded. A cross-site HTML form can send text/plain without a preflight; requiring
            // JSON keeps such a request from ever reaching the heat pump, independent of the request-header guard.
            if (!request.HasJsonContentType())
            {
                response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }

            // Only well-formed JSON is passed on, never arbitrary text.
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, context.RequestAborted);
            var json = buffer.ToArray();
            if (!IsJson(json))
            {
                await Fail(response, StatusCodes.Status400BadRequest, "invalid_json");
                return;
            }

            upstreamRequest.Content = new ByteArrayContent(json);
            upstreamRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await sensibo.CreateClient().SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (HttpRequestException)
        {
            await Fail(response, StatusCodes.Status502BadGateway, SensiboException.Unreachable);
            return;
        }
        catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await Fail(response, StatusCodes.Status504GatewayTimeout, SensiboException.Timeout);
            return;
        }

        using (upstream)
        {
            if (!upstream.IsSuccessStatusCode)
            {
                // Never pass a 401 through: the app would take it as its own sign-in expiring.
                var code = SensiboException.CodeFor(upstream.StatusCode);
                var status = upstream.StatusCode switch
                {
                    HttpStatusCode.NotFound => StatusCodes.Status404NotFound,
                    HttpStatusCode.TooManyRequests => StatusCodes.Status429TooManyRequests,
                    HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status502BadGateway,
                };
                await Fail(response, status, code);
                return;
            }

            // Sensibo's answers are small. An empty success becomes a 204, so the browser never parses an empty body.
            var body = await upstream.Content.ReadAsByteArrayAsync(context.RequestAborted);
            if (body.Length == 0)
            {
                response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            // Sensibo answers JSON. Anything else (an HTML error page from a proxy in front of it, say) is not passed
            // on, and the type is always set here rather than copied, so an answer can never render as a page of the app.
            if (upstream.Content.Headers.ContentType?.MediaType is not { } type
                || !(type.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                || !IsJson(body))
            {
                await Fail(response, StatusCodes.Status502BadGateway, SensiboException.Error);
                return;
            }

            response.StatusCode = (int)upstream.StatusCode;
            response.ContentType = "application/json; charset=utf-8";
            await response.Body.WriteAsync(body, context.RequestAborted);
        }
    }

    private static bool IsJson(byte[] utf8)
    {
        try
        {
            using var _ = JsonDocument.Parse(utf8);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Task Fail(HttpResponse response, int status, string code)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new { error = code });
    }
}

/// <summary>Read-only: there are deliberately no MCP tools that change the heat pump.</summary>
[McpServerToolType]
public sealed class HeatPumpTools(HeatPumpStateService state, IPropertyStore properties)
{
    [McpServerTool(Name = "get_heatpump_status", ReadOnly = true), Description("Latest heat pump readings from Sensibo, refreshed every 15 minutes: indoor temperature (°C), humidity (%), whether it is on, mode, target temperature, online, last seen (UTC), and the home it belongs to.")]
    public async Task<string> Status(CancellationToken ct)
    {
        var status = await state.GetStatusAsync(ct);
        var links = await state.GetLinksAsync(ct);
        var devices = new List<object>();
        foreach (var d in status.Devices)
        {
            var propertyId = links.FirstOrDefault(l => l.DeviceId == d.DeviceId)?.PropertyId;
            var property = propertyId is null ? null : await properties.GetAsync(propertyId, ct);
            devices.Add(new
            {
                id = d.DeviceId,
                name = d.Name,
                propertyId,
                propertyName = property?.Name,
                temperature = d.TemperatureC,
                humidity = d.Humidity,
                measuredAtUtc = d.MeasuredAtUtc,
                online = d.Online,
                lastSeenUtc = d.LastSeenUtc,
                on = d.On,
                mode = d.Mode,
                targetTemperature = d.TargetTemperature,
                temperatureUnit = d.TemperatureUnit,
            });
        }

        return JsonSerializer.Serialize(new { lastSuccessAtUtc = status.LastSuccessAtUtc, lastError = status.LastError, devices }, StoreJson.Options);
    }
}
