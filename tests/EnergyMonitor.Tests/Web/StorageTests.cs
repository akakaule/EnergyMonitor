using System.Net.Sockets;
using Azure.Data.Tables;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Web.Pricing;
using EnergyMonitor.Core.Model;
using EnergyMonitor.FakeUpstreams;
using EnergyMonitor.Tests.Core;
using EnergyMonitor.Web.HeatPump;
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
    [Fact]
    public async Task ImportedChargeHistory_CrossesMonths_AndReplaysReplaceRates()
    {
        var backend = CreateBackend();
        var store = new PricingStore(backend);
        var from = DateTimeOffset.Parse("2026-10-31T23:00:00Z");
        var to = from.AddHours(2);
        var rate = new TariffRate("grid", "owner", "Network", from, to, 0.5m, "kWh", "Excluded", "Eloverblik.historical");
        var link = new TariffLink("grid", "owner", "D03", from, to, 1);
        await store.SaveImportedChargesAsync("home", new([link], [rate], false), from, to, default);
        await store.SaveImportedChargesAsync("home", new([link], [rate with { Price = 0.7m }], false), from, to, default);
        var data = await new PricingStore(backend).GetImportedChargesAsync("home", from, to, default);
        Assert.Equal(2, data.Rates.Count);
        Assert.All(data.Rates, r => Assert.Equal(0.7m, r.Price));
        await store.DeleteAsync("home", default);
        Assert.Empty((await store.GetImportedChargesAsync("home", from, to, default)).Rates);
    }

    [Fact]
    public async Task LargePricingConfigurationAndEvidence_AreChunkedBelowTablePropertyLimits()
    {
        var store = new PricingStore(CreateBackend());
        var start = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var rates = Enumerable.Range(0, 1000).Select(i => new TariffRate("rate" + i, "owner", "Network", start, start.AddDays(1), 0.123456789m, "kWh", "Excluded", "manual")).ToArray();
        await store.SaveConfigurationAsync("large", new([], rates, [], false), default);
        Assert.Equal(1000, (await store.GetConfigurationAsync("large", default)).Rates.Count);
        var json = System.Text.Json.JsonSerializer.Serialize(new { description = new string('æ', 100000) });
        await store.SaveChargeEvidenceAsync("large", "latest", json, default);
        Assert.Equal(100000, (await store.GetChargeEvidenceAsync("large", default))[0].GetProperty("description").GetString()!.Length);
    }

    protected abstract ITableBackend CreateBackend();

    [Fact]
    public async Task HeatPumpReadings_MergeByTime_AcrossHoursAndMonths()
    {
        var store = new HeatPumpStore(CreateBackend());
        var end = new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.Zero);
        HeatPumpPoint Point(DateTimeOffset t, double c) => new(t.ToUnixTimeSeconds(), c, 50);

        await store.UpsertAsync("pod", [Point(end.AddHours(-1), 9), Point(end, 10)], default);
        await store.UpsertAsync("pod", [Point(end, 11), Point(end.AddMinutes(5), 12)], default);

        var all = await store.QueryAsync("pod", end.AddHours(-2), end.AddHours(1), default);
        Assert.Equal([9, 11, 12], all.Temperature.Select(p => p.Value));
        Assert.Equal(end.AddHours(-1), all.Temperature[0].Time);
        Assert.Empty((await store.QueryAsync("other", end.AddHours(-2), end.AddHours(1), default)).Temperature);
    }

    [Fact]
    public async Task HeatPumpReadings_OverLongRanges_AreAveragedIntoAtMost720Buckets()
    {
        var store = new HeatPumpStore(CreateBackend());
        var hour = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 30 * 24 * 12).Select(i => new HeatPumpPoint(hour.AddMinutes(-5 * i).ToUnixTimeSeconds(), i % 2 == 0 ? 10 : 12, null)).ToList();
        await store.UpsertAsync("pod", points, default);

        var month = await store.QueryAsync("pod", hour.AddDays(-30), hour.AddMinutes(1), default);

        Assert.InRange(month.Temperature.Count, 700, 721);
        Assert.All(month.Temperature, p => Assert.InRange(p.Value, 10, 12));
        Assert.Equal(11, month.Temperature[10].Value);
        Assert.Empty(month.Humidity);
    }

    private static Row R(string pk, string rk, string json = "{}", string? tag = null) => new(pk, rk, json, tag);

    [Fact]
    public async Task PricingHistory_RoundTripsAcrossRestart_AndCorrectionsReplace()
    {
        var backend = CreateBackend();
        var store = new PricingStore(backend);
        var start = DateTimeOffset.Parse("2026-10-31T23:45:00Z");
        var config = new PricingConfiguration([new(start, null, "DK2", "Spot", null, null, 2.123456m, 15, 0.25m)], [], ["grid"], false);
        await store.SaveConfigurationAsync("home", config, default);
        await store.SavePricesAsync([new("DK2", start, start.AddMinutes(15), -0.123456789m), new("DK2", start.AddMinutes(15), start.AddMinutes(30), 0.2m)], default);
        await store.SavePricesAsync([new("DK2", start, start.AddMinutes(15), -0.987654321m)], default);
        var restarted = new PricingStore(backend);
        Assert.Null((await restarted.GetConfigurationAsync("home", default)).Agreements[0].SupplierMarkup);
        var prices = await restarted.GetPricesAsync("DK2", start, start.AddMinutes(30), default);
        Assert.Equal(2, prices.Count);
        Assert.Equal(-0.987654321m, prices[0].DkkPerKWh);
        await restarted.DeleteAsync("home", default);
        Assert.Empty((await restarted.GetConfigurationAsync("home", default)).Agreements);
        Assert.Equal(2, (await restarted.GetPricesAsync("DK2", start, start.AddMinutes(30), default)).Count);
    }

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
        await store.RecordFailureAsync(message.Id, "Telegram answered 500.", ["1000"], ["3000"], default);
        var pending = Assert.Single(await store.ListPendingAsync(default));
        Assert.Equal((1, "Telegram answered 500."), (pending.Attempts, pending.LastError));
        Assert.Equal(["1000"], pending.DeliveredTo);
        Assert.Equal(["3000"], pending.RejectedBy);

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

public sealed class SqliteStorageTests : StorageContract, IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("em-sqlite-").FullName;

    private string File(string name = "energymonitor.db") => Path.Combine(_folder, name);

    protected override ITableBackend CreateBackend() => new SqliteTableBackend(File(Guid.NewGuid().ToString("N") + ".db"));

    [Fact]
    public async Task Restart_AgainstSqliteFile_LosesNoData_AndRepeatsNothing()
    {
        var path = File();
        var fake = new FakeState();
        var time = new FakeTimeProvider(AppFactory.Start);
        var latest = new DateOnly(2026, 11, 6);
        fake.DayFactors[(FakeState.Sommerhus, latest)] = 0.15;

        using (var first = new AppFactory(new SqliteTableBackend(path), fake, time))
        {
            await first.SeedPropertiesAsync();
            await first.Get<SchedulerService>().TickAsync(default);
            time.Advance(TimeSpan.FromMinutes(56));
            await first.Get<SchedulerService>().TickAsync(default);
            Assert.Equal(2, first.Telegrams.Count);
        }

        // A second application instance over the same file: nothing but the file is shared.
        using var second = new AppFactory(new SqliteTableBackend(path), fake, time);
        await second.Get<SchedulerService>().TickAsync(default);
        await second.SyncAsync();

        Assert.Equal(2, second.Telegrams.Count);
        var e = Assert.Single(await second.EventsAsync());
        Assert.Equal((RuleType.LowConsumption, AlertSeverity.Observer, latest), (e.RuleType, e.Severity, e.FirstLocalDate));
        var days = await second.Get<IConsumptionStore>().GetDailyAsync("sommerhus", latest.AddDays(-100), latest.AddDays(5), default);
        Assert.Equal(89, days.Count(d => d.IsComplete));
        Assert.Equal(2, (await second.Get<IPropertyStore>().ListAsync(default)).Count);
    }

    [Fact]
    public async Task Copy_MovesEveryRowOfEveryTable_WithKeysJsonAndTags()
    {
        var source = new InMemoryTableBackend();
        await source.UpsertAsync("properties", [new Row("p", "hus", "{\"name\":\"Hus\"}"), new Row("p", "sommerhus", "{}")], default);
        await source.UpsertAsync("alerts", [new Row("hus", "2026-11-11", "{\"x\":1}", Tag: "open")], default);
        var target = new SqliteTableBackend(File());

        var counts = await TableCopy.CopyAsync(source, target, ["properties", "alerts", "empty"], default);

        Assert.Equal(new Dictionary<string, int> { ["properties"] = 2, ["alerts"] = 1, ["empty"] = 0 }, counts);
        Assert.Equal(new Row("hus", "2026-11-11", "{\"x\":1}", "open"), await target.GetAsync("alerts", "hus", "2026-11-11", default));
        Assert.Equal(2, (await target.QueryAsync("properties", "p", null, null, null, default)).Count);
    }

    [Fact]
    public async Task Copy_RefusesATargetThatAlreadyHoldsData()
    {
        var target = new SqliteTableBackend(File());
        await target.UpsertAsync("state", [new Row("state", "k", "{}")], default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => TableCopy.CopyAsync(new InMemoryTableBackend(), target, ["state"], default));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }
}
