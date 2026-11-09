using System.Text.Json.Serialization;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Web.Api;
using EnergyMonitor.Web.Auth;
using EnergyMonitor.Web.Eloverblik;
using EnergyMonitor.Web.Jobs;
using EnergyMonitor.Web.Notifications;
using EnergyMonitor.Web.Storage;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Secrets (Eloverblik--RefreshToken, Telegram--BotToken, Auth--ClientSecret) come from Key Vault through
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

if (string.Equals(builder.Configuration["Storage:Mode"], "InMemory", StringComparison.OrdinalIgnoreCase))
{
    // Tests and quick local runs only: nothing survives a restart.
    builder.Services.AddSingleton<ITableBackend, InMemoryTableBackend>();
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

builder.AddEnergyMonitorAuth();
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
    if (context.Request.Path.StartsWithSegments("/api"))
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

app.MapDefaultEndpoints();
app.MapAuthEndpoints();
app.MapApi();
app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;
