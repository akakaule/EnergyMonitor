using System.Text.Json.Serialization;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Web.Api;
using EnergyMonitor.Web.Pricing;
using EnergyMonitor.Web.Auth;
using EnergyMonitor.Web.Eloverblik;
using EnergyMonitor.Web.HeatPump;
using EnergyMonitor.Web.Jobs;
using EnergyMonitor.Web.Notifications;
using EnergyMonitor.Web.Storage;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

if (args is ["copy-tables", ..])
{
    return await CopyTablesAsync(args);
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Secrets (Eloverblik--RefreshToken, Telegram--BotToken, Sensibo--ApiKey, Auth--ClientSecret) come from Key Vault through
// the app's managed identity. Reloading lets a renewed Eloverblik token take effect without a restart.
if (builder.Configuration["KeyVault:Uri"] is { Length: > 0 } vaultUri)
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(vaultUri),
        new DefaultAzureCredential(),
        new AzureKeyVaultConfigurationOptions { ReloadInterval = TimeSpan.FromMinutes(30) });
}

if (builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 })
{
    builder.Logging.AddOpenTelemetry(logging => logging.AddAzureMonitorLogExporter());
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing.AddAzureMonitorTraceExporter())
        .WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter());
}

builder.Services.Configure<EloverblikOptions>(builder.Configuration.GetSection("Eloverblik"));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection("Sync"));
builder.Services.Configure<ScheduleOptions>(builder.Configuration.GetSection("Schedule"));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);

var storageMode = builder.Configuration["Storage:Mode"];
if (string.Equals(storageMode, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    // Tests and quick local runs only: nothing survives a restart.
    builder.Services.AddSingleton<ITableBackend, InMemoryTableBackend>();
}
else if (string.Equals(storageMode, "Sqlite", StringComparison.OrdinalIgnoreCase))
{
    // A single private machine: everything in one file (Storage:Path), backed up by copying that file.
    var path = builder.Configuration["Storage:Path"] is { Length: > 0 } configured
        ? configured
        : throw new InvalidOperationException("Storage:Path must be set when Storage:Mode is Sqlite.");
    builder.Services.AddSingleton<ITableBackend>(new SqliteTableBackend(path));
}
else
{
    builder.AddAzureTableServiceClient("tables");
    builder.Services.AddSingleton<ITableBackend>(services => new AzureTableBackend(services.GetRequiredService<Azure.Data.Tables.TableServiceClient>()));
}

builder.Services.AddSingleton<IPropertyStore, PropertyStore>();
builder.Services.AddSingleton<IConsumptionStore, ConsumptionStore>();
builder.Services.AddSingleton<IAlertStore, AlertStore>();
builder.Services.AddSingleton<ISyncRunStore, SyncRunStore>();
builder.Services.AddSingleton<IStateStore, StateStore>();
builder.Services.AddSingleton<IOutboxStore, OutboxStore>();

builder.Services.AddHttpClient(EloverblikClient.HttpClientName, (services, client) =>
{
    var baseUrl = services.GetRequiredService<IOptionsMonitor<EloverblikOptions>>().CurrentValue.BaseUrl;
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(100);
});

// The Telegram request path contains the bot token, so this client writes no request logs.
builder.Services.AddHttpClient(TelegramChannel.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
    .RemoveAllLoggers();

builder.Services.AddSingleton<IDelayer, TaskDelayer>();
builder.Services.AddSingleton<EloverblikClient>();
builder.Services.AddSingleton<EloverblikTokenStatus>();
builder.Services.AddSingleton<IMeasurementSource, EloverblikMeasurementSource>();
builder.Services.AddSingleton<INotificationChannel, TelegramChannel>();
builder.Services.AddSingleton<NotificationSettingsService>();
builder.Services.AddSingleton<SummaryService>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<OutboxDispatcher>();
builder.Services.AddSingleton<MonitorPipeline>();
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddHostedService(services => services.GetRequiredService<SchedulerService>());

builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection("Pricing"));
builder.Services.AddSingleton<PricingStore>();
builder.Services.AddSingleton<EnergyQueryService>();
builder.Services.AddSingleton<PriceSyncService>();
builder.Services.AddSingleton<SpotPriceClient>();
builder.Services.AddHttpClient("spotprices", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Pricing:BaseUrl"] ?? "https://api.energidataservice.dk/");
    client.Timeout = TimeSpan.FromSeconds(100);
});
builder.AddHeatPump();
builder.AddEnergyMonitorAuth();
builder.AddEnergyMcp();
builder.Services.ConfigureApiChallenges();

var app = builder.Build();

// Behind the Container Apps ingress, ASPNETCORE_FORWARDEDHEADERS_ENABLED=true (set in Bicep) makes the app see
// the original https scheme, which the sign-in redirect needs.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers.ContentSecurityPolicy =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/mcp"))
    {
        // Consumption data must not be kept by shared caches or the service worker's HTTP cache.
        headers.CacheControl = "no-store";
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseApiGuards();
app.UseAuthorization();
if (EnergyMonitor.Web.HeatPump.HeatPumpEndpoints.IsEnabled(app.Configuration))
{
    app.UseRateLimiter();
}

app.MapDefaultEndpoints();
app.MapAuthEndpoints();
app.MapApi();
app.MapPricing();
app.MapHeatPump();
app.MapEnergyMcp();
app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();
return 0;

/// <summary>
/// One-time move off Azure: <c>copy-tables --from https://account.table.core.windows.net --to energymonitor.db</c>.
/// Signs in with the developer's Azure credentials and copies every table of the app into a new SQLite file.
/// </summary>
static async Task<int> CopyTablesAsync(string[] args)
{
    string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
    if (Option("--from") is not { Length: > 0 } from || Option("--to") is not { Length: > 0 } to)
    {
        Console.Error.WriteLine("Usage: copy-tables --from <table endpoint URL> --to <new SQLite file>");
        return 2;
    }

    const string prefix = "em";
    var service = new Azure.Data.Tables.TableServiceClient(new Uri(from), new DefaultAzureCredential());
    var tables = new List<string>();
    await foreach (var table in service.QueryAsync(filter: (string?)null))
    {
        if (table.Name.StartsWith(prefix, StringComparison.Ordinal))
        {
            tables.Add(table.Name[prefix.Length..]);
        }
    }

    var counts = await TableCopy.CopyAsync(new AzureTableBackend(service, prefix), new SqliteTableBackend(to), tables, CancellationToken.None);
    foreach (var (table, count) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"{prefix}{table}	{count}");
    }

    Console.WriteLine($"{counts.Values.Sum()} rows in {counts.Count} tables copied to {Path.GetFullPath(to)}");
    return 0;
}

public partial class Program;
