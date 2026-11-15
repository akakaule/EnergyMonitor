using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.FakeUpstreams;
using EnergyMonitor.Web.Auth;
using EnergyMonitor.Web.Eloverblik;
using EnergyMonitor.Web.HeatPump;
using EnergyMonitor.Web.Jobs;
using EnergyMonitor.Web.Notifications;
using EnergyMonitor.Web.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace EnergyMonitor.Tests.Web;

public sealed class RecordingDelayer : IDelayer
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

/// <summary>Collects every log line, with exception text, so tests can assert what must never be logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public string Text => string.Join('\n', _lines);

    public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Capturing(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {state} {exception}");
        }
    }
}

/// <summary>
/// The real application in-process: in-memory (or supplied) table backend, a controllable clock, and the
/// fake Eloverblik and Telegram services wired in as the HTTP handlers of the app's own clients.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string Admin = "admin@test";
    public const string Viewer = "viewer@test";
    public const string Stranger = "stranger@test";
    public const string RefreshToken = "refresh-TOKEN-do-not-log";
    public const string BotToken = "424242:BOT-TOKEN-do-not-log";
    public const string ChatId = "1000";

    /// <summary>08:05 in Copenhagen on Sunday 8 November 2026.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 8, 7, 5, 0, TimeSpan.Zero);

    private readonly WebApplication _fake;

    public AppFactory(ITableBackend? backend = null, FakeState? fake = null, FakeTimeProvider? time = null, bool pricing = false, bool mcp = false, bool local = false, bool heatpump = false)
    {
        Pricing = pricing; Mcp = mcp; Local = local; HeatPump = heatpump;
        Backend = backend ?? new InMemoryTableBackend();
        Time = time ?? new FakeTimeProvider(Start);
        Fake = fake ?? new FakeState();
        Fake.Now = () => Time.GetUtcNow();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(Fake);
        _fake = builder.Build();
        _fake.MapFakeUpstreams();
        _fake.StartAsync().GetAwaiter().GetResult();
    }

    private bool Pricing { get; }
    private bool Mcp { get; }
    private bool HeatPump { get; }

    /// <summary>Runs as on the private Linux machine: Production environment, no sign-in, no OAuth for MCP.</summary>
    private bool Local { get; }
    public ITableBackend Backend { get; }

    public FakeTimeProvider Time { get; }

    public FakeState Fake { get; }

    public RecordingDelayer Delayer { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Local ? "Production" : "Development");
        builder.UseSetting("Pricing:Enabled", Pricing.ToString());
        builder.UseSetting("Mcp:Enabled", Mcp.ToString());
        if (!Local)
        {
            builder.UseSetting("Mcp:Authority", "https://issuer.test");
            builder.UseSetting("Mcp:Audience", "energy");
            builder.UseSetting("Mcp:Resource", "https://energy.test/mcp");
        }

        builder.UseSetting("Storage:Mode", "InMemory");
        builder.UseSetting("Schedule:Enabled", "false");
        builder.UseSetting("Auth:Mode", Local ? AuthOptions.ModeNone : AuthOptions.ModeDevelopment);
        builder.UseSetting("Auth:DevUser", "");
        builder.UseSetting("Auth:Users:0:Subject", Admin);
        builder.UseSetting("Auth:Users:0:Role", UserRoles.Admin);
        builder.UseSetting("Auth:Users:1:Subject", Viewer);
        builder.UseSetting("Auth:Users:1:Role", UserRoles.Viewer);
        builder.UseSetting("Eloverblik:BaseUrl", "http://fake/customerapi/api/");
        builder.UseSetting("Eloverblik:RefreshToken", RefreshToken);
        builder.UseSetting("Telegram:BaseUrl", "http://fake");
        builder.UseSetting("Telegram:BotToken", BotToken);
        builder.UseSetting("Telegram:ChatIds:0", ChatId);
        builder.UseSetting("Sensibo:Enabled", HeatPump.ToString());
        builder.UseSetting("Sensibo:BaseUrl", "http://fake/sensibo/api/v2/");
        builder.UseSetting("Sensibo:ApiKey", FakeSensibo.ApiKey);

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITableBackend>();
            services.AddSingleton(Backend);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<IDelayer>();
            services.AddSingleton<IDelayer>(Delayer);
            services.AddHttpClient(EloverblikClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _fake.GetTestServer().CreateHandler());
            services.AddHttpClient(TelegramChannel.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _fake.GetTestServer().CreateHandler());
            services.AddHttpClient(SensiboClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _fake.GetTestServer().CreateHandler());
        });
    }

    public HttpClient ClientFor(string? user)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(DevAuthHandler.Header, user);
        }

        client.DefaultRequestHeaders.Add(AuthSetup.CsrfHeader, AuthSetup.CsrfValue);
        return client;
    }

    /// <summary>Creates Hjem and Sommerhus linked to the fake's two metering points.</summary>
    public async Task SeedPropertiesAsync()
    {
        var store = Get<IPropertyStore>();
        var created = Time.GetUtcNow().AddDays(-200);
        await store.UpsertAsync(new Property("hjem", "Hjem", FakeState.Hjem, true, Property.DefaultTimeZone, created), default);
        await store.UpsertAsync(new Property("sommerhus", "Sommerhus", FakeState.Sommerhus, true, Property.DefaultTimeZone, created.AddSeconds(1)), default);
    }

    /// <summary>Runs sync, rules and delivery. The clock moves a second first, as no two real runs share an instant.</summary>
    public Task<SyncRun?> SyncAsync()
    {
        Time.Advance(TimeSpan.FromSeconds(1));
        return Get<MonitorPipeline>().RunSyncAsync(default);
    }

    /// <summary>Links the fake's heat pump to Sommerhus.</summary>
    public Task LinkHeatPumpAsync() =>
        Get<HeatPumpStateService>().SetLinksAsync([new HeatPumpLink(FakeSensibo.DeviceId, "sommerhus")], default);

    /// <summary>Runs one heat pump collection (record, rules) and delivery, a second after the previous step.</summary>
    public async Task CollectHeatPumpAsync(TimeSpan? after = null)
    {
        Time.Advance(after ?? TimeSpan.FromSeconds(1));
        await Get<HeatPumpCollector>().CollectAsync(default);
        await Get<OutboxDispatcher>().DispatchAsync(default);
    }

    public async Task<IReadOnlyList<AlertEvent>> EventsAsync(string propertyId = "sommerhus") =>
        await Get<IAlertStore>().GetEventsAsync(propertyId, default);

    public List<string> Telegrams => Fake.Locked(() => Fake.TelegramMessages.Select(m => m.Text).ToList());

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _fake.StopAsync().GetAwaiter().GetResult();
            _fake.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

internal static class HttpJson
{
    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
