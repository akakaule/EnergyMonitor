using System.Net.Sockets;
using Azure.Data.Tables;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.FakeUpstreams;
using EnergyMonitor.Tests.Core;
using EnergyMonitor.Web.Jobs;
using EnergyMonitor.Web.Storage;
using Microsoft.Extensions.Time.Testing;

namespace EnergyMonitor.Tests.Web;

/// <summary>
/// The same behaviour is required of the in-memory backend used in tests and of real Table Storage.
/// The Azurite variants run when an emulator listens on 127.0.0.1:10002 (CI starts one) and are skipped otherwise.
/// </summary>
public abstract class StorageContract
{
    protected abstract ITableBackend CreateBackend();

    private static Row R(string pk, string rk, string json = "{}", string? tag = null) => new(pk, rk, json, tag);

    [Fact]
    public async Task Upsert_Replaces_And_Get_ReturnsTheLatest()
    {
        var backend = CreateBackend();

        await backend.UpsertAsync("t1", [R("p", "a", "1"), R("p", "b", "2", "tag")], default);
        await backend.UpsertAsync("t1", [R("p", "a", "3")], default);

        Assert.Equal("3", (await backend.GetAsync("t1", "p", "a", default))!.Json);
        Assert.Equal("tag", (await backend.GetAsync("t1", "p", "b", default))!.Tag);
        Assert.Null(await backend.GetAsync("t1", "p", "missing", default));
        Assert.Null(await backend.GetAsync("other", "p", "a", default));
    }

    [Fact]
    public async Task TryAdd_InsertsOnce()
    {
        var backend = CreateBackend();

        Assert.True(await backend.TryAddAsync("t1", R("p", "a", "first"), default));
        Assert.False(await backend.TryAddAsync("t1", R("p", "a", "second"), default));
        Assert.Equal("first", (await backend.GetAsync("t1", "p", "a", default))!.Json);
    }

    [Fact]
    public async Task Query_IsOrdered_RangeIsHalfOpen_AndTakeLimits()
    {
        var backend = CreateBackend();
        await backend.UpsertAsync("t1", [R("p", "2026-11-03"), R("p", "2026-11-01"), R("p", "2026-11-02"), R("q", "2026-11-02")], default);

        Assert.Equal(["2026-11-01", "2026-11-02", "2026-11-03"], (await backend.QueryAsync("t1", "p", null, null, null, default)).Select(r => r.Rk));
        Assert.Equal(["2026-11-01", "2026-11-02"], (await backend.QueryAsync("t1", "p", "2026-11-01", "2026-11-03", null, default)).Select(r => r.Rk));
        Assert.Equal(["2026-11-01"], (await backend.QueryAsync("t1", "p", null, null, 1, default)).Select(r => r.Rk));
    }

    [Fact]
    public async Task LargeBatches_AcrossPartitions_AreWrittenAndDeleted()
    {
        var backend = CreateBackend();
        var rows = Enumerable.Range(0, 250).Select(i => R(i % 2 == 0 ? "a|x" : "a|y", $"{i:D4}", i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        rows.Add(R("b|x", "0001"));

        await backend.UpsertAsync("t1", rows, default);
        var mine = await backend.QueryPartitionsAsync("t1", "a|", "a|~", default);
        Assert.Equal(250, mine.Count);

        await backend.DeleteAsync("t1", mine, default);
        await backend.DeleteAsync("t1", mine, default); // deleting what is already gone is not an error

        Assert.Empty(await backend.QueryPartitionsAsync("t1", "a|", "a|~", default));
        Assert.Single(await backend.QueryAsync("t1", "b|x", null, null, null, default));
    }

    [Fact]
    public async Task ConsumptionStore_RoundTripsIntervalsAndDays_AcrossMonths_Idempotently()
    {
        var store = new ConsumptionStore(CreateBackend());
        var days = new[] { new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 1) };
        var intervals = days.SelectMany(d => TestData.HourlyDay(d)).ToList();
        intervals[5] = intervals[5] with { KWh = null, Quality = IntervalQuality.Missing };

        await store.UpsertIntervalsAsync(intervals, default);
        await store.UpsertIntervalsAsync(intervals, default);
        await store.UpsertIntervalsAsync([intervals[0] with { KWh = 9.5 }], default);

        var read = await store.GetIntervalsAsync("sommerhus", "eloverblik", intervals[0].StartUtc, intervals[^1].EndUtc, default);
        Assert.Equal(48, read.Count);
        Assert.Equal(9.5, read.Single(i => i.StartUtc == intervals[0].StartUtc).KWh);
        var missing = read.Single(i => i.StartUtc == intervals[5].StartUtc);
        Assert.Equal((IntervalQuality.Missing, (double?)null), (missing.Quality, missing.KWh));
        Assert.Equal(24, (await store.GetIntervalsAsync("sommerhus", "eloverblik", intervals[24].StartUtc, intervals[^1].EndUtc, default)).Count);
        Assert.Empty(await store.GetIntervalsAsync("hjem", "eloverblik", intervals[0].StartUtc, intervals[^1].EndUtc, default));

        await store.UpsertDailyAsync([TestData.Day(days[0], 7.5), TestData.Day(days[1], null, DayQuality.Missing)], default);
        var daily = await store.GetDailyAsync("sommerhus", days[0], days[1], default);
        Assert.Equal([(days[0], 7.5, DayQuality.Complete), (days[1], null, DayQuality.Missing)], daily.Select(d => (d.LocalDate, d.KWh, d.Quality)));
        Assert.Single(await store.GetDailyAsync("sommerhus", days[1], days[1], default));

        await store.DeleteAllAsync("sommerhus", default);
        Assert.Empty(await store.GetIntervalsAsync("sommerhus", "eloverblik", intervals[0].StartUtc, intervals[^1].EndUtc, default));
        Assert.Empty(await store.GetDailyAsync("sommerhus", days[0], days[1], default));
    }

    [Fact]
    public async Task AlertStore_CreatesAnEventOnce_AndKeepsItsSnapshot()
    {
        var store = new AlertStore(CreateBackend());
        var e = new AlertEvent(
            "sommerhus", "LowConsumption", RuleType.LowConsumption, new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 6),
            AlertSeverity.Observer, AlertState.Open, 1.2, 7.4, 3.7, "forklaring", new RuleParams { ThresholdPercent = 50 }, TestData.Created);

        Assert.True(await store.TryCreateEventAsync(e, default));
        Assert.False(await store.TryCreateEventAsync(e with { Explanation = "dublet" }, default));
        await store.UpdateEventAsync(e with { Severity = AlertSeverity.Alarm, LastNotifiedSeverity = AlertSeverity.Observer, AcknowledgedAtUtc = TestData.Created }, default);

        var read = await store.GetEventAsync(e.Id, default);
        Assert.Equal(e with { Severity = AlertSeverity.Alarm, LastNotifiedSeverity = AlertSeverity.Observer, AcknowledgedAtUtc = TestData.Created }, read);
        Assert.Single(await store.GetEventsAsync("sommerhus", default));
        Assert.Null(await store.GetEventAsync("sommerhus~LowConsumption~2020-01-01", default));

        var rule = AlertRule.Default("sommerhus", RuleType.HighConsumption, TestData.Created) with { Enabled = false };
        await store.UpsertRuleAsync(rule, default);
        Assert.Equal(rule, Assert.Single(await store.GetRulesAsync("sommerhus", default)));
    }

    [Fact]
    public async Task SyncRuns_AreReturnedNewestFirst()
    {
        var store = new SyncRunStore(CreateBackend());
        foreach (var hour in new[] { 3, 1, 2 })
        {
            await store.SaveAsync(new SyncRun($"run{hour}", "eloverblik", TestData.Created.AddHours(hour), null, SyncStatus.Running, 0, null), default);
        }

        await store.SaveAsync(new SyncRun("run3", "eloverblik", TestData.Created.AddHours(3), TestData.Created.AddHours(4), SyncStatus.Failed, 0, "http_503"), default);

        var recent = await store.GetRecentAsync("eloverblik", 2, default);
        Assert.Equal(["run3", "run2"], recent.Select(r => r.Id));
        Assert.Equal((SyncStatus.Failed, "http_503"), (recent[0].Status, recent[0].ErrorCode));
    }

    [Fact]
    public async Task Outbox_QueuesAMessageIdOnlyOnce_EvenAfterItWasSent()
    {
        var store = new OutboxStore(CreateBackend());
        var message = new OutboxMessage("alert|x|Observer", "tekst", TestData.Created);

        Assert.True(await store.TryEnqueueAsync(message, default));
        Assert.False(await store.TryEnqueueAsync(message, default));
        await store.RecordFailureAsync(message.Id, "Telegram answered 500.", default);
        Assert.Equal((1, "Telegram answered 500."), ((await store.ListPendingAsync(default))[0].Attempts, (await store.ListPendingAsync(default))[0].LastError));

        await store.MarkSentAsync(message.Id, default);

        Assert.Empty(await store.ListPendingAsync(default));
        Assert.False(await store.TryEnqueueAsync(message, default));
    }

    [Fact]
    public async Task State_ClaimsAKeyOnce()
    {
        var store = new StateStore(CreateBackend());

        Assert.True(await store.TryClaimAsync("slot|sync|2026-11-08|08:00", default));
        Assert.False(await store.TryClaimAsync("slot|sync|2026-11-08|08:00", default));
        await store.SetAsync("k", "\"v\"", default);
        Assert.Equal("\"v\"", await store.GetAsync("k", default));
        await store.DeleteAsync("k", default);
        Assert.Null(await store.GetAsync("k", default));
    }
}

public class InMemoryStorageTests : StorageContract
{
    protected override ITableBackend CreateBackend() => new InMemoryTableBackend();
}

public class AzuriteStorageTests : StorageContract
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("EM_TEST_TABLES") ?? "UseDevelopmentStorage=true";

    public static bool Available { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync("127.0.0.1", 10002).Wait(TimeSpan.FromMilliseconds(500)) && tcp.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    /// <summary>A backend with its own set of tables, so tests do not see each other's rows.</summary>
    public static ITableBackend NewBackend()
    {
        Assert.SkipUnless(Available, "No Azurite table endpoint on 127.0.0.1:10002.");
        return new AzureTableBackend(new TableServiceClient(ConnectionString), "t" + Guid.NewGuid().ToString("N")[..12]);
    }

    protected override ITableBackend CreateBackend() => NewBackend();

    [Fact]
    public async Task Restart_AgainstTableStorage_LosesNoData_AndRepeatsNothing()
    {
        var backend = NewBackend();
        var fake = new FakeState();
        var time = new FakeTimeProvider(AppFactory.Start);
        var latest = new DateOnly(2026, 11, 6);
        fake.DayFactors[(FakeState.Sommerhus, latest)] = 0.15;

        using (var first = new AppFactory(backend, fake, time))
        {
            await first.SeedPropertiesAsync();
            await first.Get<SchedulerService>().TickAsync(default);
            time.Advance(TimeSpan.FromMinutes(56));
            await first.Get<SchedulerService>().TickAsync(default);
            Assert.Equal(2, first.Telegrams.Count);
        }

        // A second application instance over a second client: nothing but the tables is shared.
        var reopened = new AzureTableBackend(new TableServiceClient(ConnectionString), ((AzureTableBackend)backend).Prefix);
        using var second = new AppFactory(reopened, fake, time);
        await second.Get<SchedulerService>().TickAsync(default);
        await second.SyncAsync();

        Assert.Equal(2, second.Telegrams.Count);
        var e = Assert.Single(await second.EventsAsync());
        Assert.Equal((RuleType.LowConsumption, AlertSeverity.Observer, latest), (e.RuleType, e.Severity, e.FirstLocalDate));
        var days = await second.Get<IConsumptionStore>().GetDailyAsync("sommerhus", latest.AddDays(-100), latest.AddDays(5), default);
        Assert.Equal(89, days.Count(d => d.IsComplete));
        Assert.Equal(2, (await second.Get<IPropertyStore>().ListAsync(default)).Count);
    }
}
