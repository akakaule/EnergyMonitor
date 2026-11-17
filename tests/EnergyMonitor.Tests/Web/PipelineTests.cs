using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;
using EnergyMonitor.FakeUpstreams;
using EnergyMonitor.Tests.Core;
using EnergyMonitor.Web.Eloverblik;
using EnergyMonitor.Web.Jobs;
using EnergyMonitor.Web.Storage;
using Microsoft.Extensions.Time.Testing;

namespace EnergyMonitor.Tests.Web;

/// <summary>
/// End-to-end behaviour of sync → aggregation → rules → notification, with the real application wired to
/// the fake Eloverblik and Telegram. "Today" is 8 November 2026; the fake has data through 6 November.
/// </summary>
public class PipelineTests
{
    private static readonly DateOnly Today = new(2026, 11, 8);
    private static readonly DateOnly Latest = new(2026, 11, 6);

    private static async Task<AppFactory> StartAsync(Action<FakeState>? arrange = null)
    {
        var app = new AppFactory();
        arrange?.Invoke(app.Fake);
        await app.SeedPropertiesAsync();
        return app;
    }

    private static Task<IReadOnlyList<DailyConsumption>> DaysAsync(AppFactory app, string propertyId = "sommerhus") =>
        app.Get<IConsumptionStore>().GetDailyAsync(propertyId, Today.AddDays(-120), Today.AddDays(30), default);

    private static async Task<int> IntervalCountAsync(AppFactory app, string propertyId = "sommerhus") =>
        (await app.Get<IConsumptionStore>().GetIntervalsAsync(
            propertyId, EloverblikMeasurementSource.Id, AppFactory.Start.AddDays(-200), AppFactory.Start.AddDays(30), default)).Count;

    [Fact]
    public async Task FirstSync_Imports90Days_InChunks_AndMarksYesterdayMissingNotZero()
    {
        using var app = await StartAsync();

        var run = await app.SyncAsync();

        Assert.Equal(SyncStatus.Succeeded, run!.Status);
        Assert.Null(run.ErrorCode);
        var days = await DaysAsync(app);
        Assert.Equal(90, days.Count);
        Assert.Equal(Today.AddDays(-90), days[0].LocalDate);
        Assert.Equal(89, days.Count(d => d.Quality == DayQuality.Complete));

        var yesterday = days[^1];
        Assert.Equal(Today.AddDays(-1), yesterday.LocalDate);
        Assert.Equal(DayQuality.Missing, yesterday.Quality);
        Assert.Null(yesterday.KWh);

        // 89 days of hours, plus the extra hour on 25 October, for each of the two properties.
        Assert.Equal(2 * ((89 * 24) + 1), run.IntervalCount);
        Assert.Equal(6, app.Fake.TimeSeriesCalls); // 3 chunks of 30 days × 2 properties
        Assert.Equal(1, app.Fake.TokenCalls);
    }

    [Fact]
    public async Task DstDays_AreCompleteWith23And25Hours()
    {
        // In March the spring change is inside the 90-day window.
        using var app = new AppFactory(time: new FakeTimeProvider(new DateTimeOffset(2026, 4, 10, 7, 5, 0, TimeSpan.Zero)));
        await app.SeedPropertiesAsync();
        await app.SyncAsync();
        var spring = (await app.Get<IConsumptionStore>().GetDailyAsync("sommerhus", new DateOnly(2026, 3, 29), new DateOnly(2026, 3, 29), default)).Single();

        using var autumn = await StartAsync();
        await autumn.SyncAsync();
        var fall = (await DaysAsync(autumn)).Single(d => d.LocalDate == new DateOnly(2026, 10, 25));

        Assert.Equal((DayQuality.Complete, 23, 23), (spring.Quality, spring.ExpectedIntervals, spring.ReceivedIntervals));
        Assert.Equal((DayQuality.Complete, 25, 25), (fall.Quality, fall.ExpectedIntervals, fall.ReceivedIntervals));
    }

    [Fact]
    public async Task RepeatedSync_IsIdempotent_AndOnlyRefetchesSevenDays()
    {
        using var app = await StartAsync();
        await app.SyncAsync();
        var intervals = await IntervalCountAsync(app);
        var days = await DaysAsync(app);
        var calls = app.Fake.TimeSeriesCalls;

        await app.SyncAsync();
        await app.SyncAsync();

        Assert.Equal(intervals, await IntervalCountAsync(app));
        Assert.Equal(days.Select(d => (d.LocalDate, d.KWh, d.Quality)), (await DaysAsync(app)).Select(d => (d.LocalDate, d.KWh, d.Quality)));
        Assert.Equal(calls + 4, app.Fake.TimeSeriesCalls); // one 7-day request per property per sync
    }

    [Fact]
    public async Task CorrectedMeasurements_ReplaceStoredValues_AndTheDayIsRecomputed()
    {
        using var app = await StartAsync();
        await app.SyncAsync();
        var day = new DateOnly(2026, 11, 4);
        var before = (await DaysAsync(app)).Single(d => d.LocalDate == day).KWh!.Value;
        var intervals = await IntervalCountAsync(app);

        app.Fake.DayFactors[(FakeState.Sommerhus, day)] = 2;
        await app.SyncAsync();

        var after = (await DaysAsync(app)).Single(d => d.LocalDate == day).KWh!.Value;
        Assert.Equal(before * 2, after, 1);
        Assert.Equal(intervals, await IntervalCountAsync(app));
    }

    [Fact]
    public async Task LowDay_RaisesOneObserverAndOneMessage_HoweverManyTimesWeSync_ThenEscalates()
    {
        using var app = await StartAsync();
        await app.SyncAsync();
        Assert.Empty(await app.EventsAsync());

        app.Fake.ApplyScenario("low");
        await app.SyncAsync();
        await app.SyncAsync();
        await app.SyncAsync();

        var observer = Assert.Single(await app.EventsAsync());
        Assert.Equal((RuleType.LowConsumption, AlertSeverity.Observer, AlertState.Open, Latest), (observer.RuleType, observer.Severity, observer.State, observer.FirstLocalDate));
        Assert.NotNull(observer.Baseline);
        Assert.True(observer.Observed < observer.Threshold);
        var first = Assert.Single(app.Telegrams);
        Assert.Contains("Sommerhus", first);
        Assert.Contains("Observer", first);
        Assert.Empty(await app.EventsAsync("hjem"));

        // The next day is low as well: the same event escalates and one more message is sent.
        app.Time.Advance(TimeSpan.FromDays(1));
        app.Fake.DayFactors[(FakeState.Sommerhus, Latest.AddDays(1))] = 0.15;
        await app.SyncAsync();
        await app.SyncAsync();

        var alarm = Assert.Single(await app.EventsAsync());
        Assert.Equal((AlertSeverity.Alarm, Latest, Latest.AddDays(1)), (alarm.Severity, alarm.FirstLocalDate, alarm.LastLocalDate));
        Assert.Equal(2, app.Telegrams.Count);
        Assert.Contains("Alarm", app.Telegrams[1]);
    }

    [Fact]
    public async Task MissingData_RaisesItsOwnAlarm_NotALowConsumptionAlarm_AndClosesWhenDataReturns()
    {
        using var app = await StartAsync();
        await app.SyncAsync();

        app.Fake.MissingFrom[FakeState.Sommerhus] = Latest.AddDays(1);
        app.Time.Advance(TimeSpan.FromDays(3));
        await app.SyncAsync();
        await app.SyncAsync();

        var missing = Assert.Single(await app.EventsAsync());
        Assert.Equal((RuleType.MissingData, Latest.AddDays(1)), (missing.RuleType, missing.FirstLocalDate));
        Assert.Contains("Data mangler", Assert.Single(app.Telegrams));
        Assert.Empty(await app.EventsAsync("hjem"));
        Assert.All((await DaysAsync(app)).Where(d => d.LocalDate > Latest), d => Assert.Null(d.KWh));

        app.Fake.MissingFrom.Clear();
        await app.SyncAsync();

        Assert.Equal(AlertState.Closed, Assert.Single(await app.EventsAsync()).State);
        Assert.Single(app.Telegrams);
    }

    [Fact]
    public async Task PartialDay_IsPartial_AndRaisesNoLowAlarm()
    {
        using var app = await StartAsync(fake => fake.PartialDays[(FakeState.Sommerhus, Latest)] = 9);

        await app.SyncAsync();

        var day = (await DaysAsync(app)).Single(d => d.LocalDate == Latest);
        Assert.Equal((DayQuality.Partial, 24, 9), (day.Quality, day.ExpectedIntervals, day.ReceivedIntervals));
        Assert.Empty(await app.EventsAsync());
        Assert.Empty(app.Telegrams);
    }

    [Fact]
    public async Task EmptyTimeSeries_BecomesMissingDays_NeverZero()
    {
        using var app = await StartAsync(fake => fake.MissingFrom[FakeState.Sommerhus] = DateOnly.MinValue);

        var run = await app.SyncAsync();

        Assert.Equal(SyncStatus.Succeeded, run!.Status);
        var days = await DaysAsync(app);
        Assert.Equal(90, days.Count);
        Assert.All(days, d => Assert.Equal((DayQuality.Missing, (double?)null), (d.Quality, d.KWh)));
        Assert.DoesNotContain(await app.EventsAsync(), e => e.RuleType == RuleType.LowConsumption);
    }

    [Fact]
    public async Task DailyOnlyMeteringPoint_IsStoredAsDailyValues()
    {
        using var app = await StartAsync(fake => fake.DailyOnly.Add(FakeState.Sommerhus));

        await app.SyncAsync();

        var day = (await DaysAsync(app)).Single(d => d.LocalDate == Latest);
        Assert.Equal((DayQuality.Complete, 1, 1), (day.Quality, day.ExpectedIntervals, day.ReceivedIntervals));
        Assert.Equal(89, await IntervalCountAsync(app));
    }

    [Fact]
    public async Task Throttled429_IsRetriedAfterAMinute_AndTheSyncSucceeds()
    {
        using var app = await StartAsync(fake =>
        {
            fake.FailStatus = 429;
            fake.FailuresLeft = 1;
        });

        var run = await app.SyncAsync();

        Assert.Equal(SyncStatus.Succeeded, run!.Status);
        Assert.Equal([TimeSpan.FromSeconds(60)], app.Delayer.Delays);
    }

    [Fact]
    public async Task Outage503_FailsTheRun_AndThreeFailuresRaiseOneTechnicalAlarm()
    {
        using var app = await StartAsync();
        await app.SyncAsync();
        app.Fake.ApplyScenario("outage");

        var run = await app.SyncAsync();
        Assert.Equal((SyncStatus.Failed, "http_503"), (run!.Status, run.ErrorCode));
        Assert.Empty(await app.EventsAsync(AlertEvent.SystemPropertyId));

        await app.SyncAsync();
        await app.SyncAsync();
        await app.SyncAsync();

        var technical = Assert.Single(await app.EventsAsync(AlertEvent.SystemPropertyId));
        Assert.Equal(RuleType.SourceFailure, technical.RuleType);
        Assert.Contains("Teknisk alarm", Assert.Single(app.Telegrams));
        Assert.Empty(await app.EventsAsync()); // an outage is not a consumption alarm

        app.Fake.ApplyScenario("normal");
        await app.SyncAsync();

        Assert.Equal(AlertState.Closed, Assert.Single(await app.EventsAsync(AlertEvent.SystemPropertyId)).State);
    }

    [Fact]
    public async Task RejectedRefreshToken_FailsWithTokenInvalid_AndRaisesTechnicalAlarmAtOnce()
    {
        using var app = await StartAsync(fake => fake.RefreshTokenRejected = true);

        var run = await app.SyncAsync();

        Assert.Equal((SyncStatus.Failed, "token_invalid"), (run!.Status, run.ErrorCode));
        Assert.Equal(RuleType.SourceFailure, Assert.Single(await app.EventsAsync(AlertEvent.SystemPropertyId)).RuleType);
        Assert.Empty(await DaysAsync(app)); // nothing was fetched, and nothing was invented
    }

    [Fact]
    public async Task TelegramFailure_KeepsTheMessageForRetry_WithoutDuplicatingTheEvent()
    {
        using var app = await StartAsync();
        await app.SyncAsync();
        app.Fake.ApplyScenario("low");
        app.Fake.ApplyScenario("telegram-down");

        await app.SyncAsync();
        await app.SyncAsync();

        Assert.Single(await app.EventsAsync());
        Assert.Empty(app.Telegrams);
        var pending = Assert.Single(await app.Get<IOutboxStore>().ListPendingAsync(default));
        Assert.Equal(2, pending.Attempts);
        Assert.Equal("Telegram answered 500.", pending.LastError);

        app.Fake.ApplyScenario("telegram-up");
        await app.Get<OutboxDispatcher>().DispatchAsync(default);
        await app.Get<OutboxDispatcher>().DispatchAsync(default);
        await app.SyncAsync();

        Assert.Single(app.Telegrams);
        Assert.Single(await app.EventsAsync());
        Assert.Empty(await app.Get<IOutboxStore>().ListPendingAsync(default));
    }

    [Fact]
    public async Task OneFailingRecipient_DoesNotMakeTheOthersGetTheMessageAgain()
    {
        using var app = await StartAsync();
        await app.Get<NotificationSettingsService>().SetAsync(new NotificationSettings(true, ["1000", "2000", "3000"]), default);
        var outbox = app.Get<IOutboxStore>();
        var dispatcher = app.Get<OutboxDispatcher>();
        app.Fake.Locked(() => app.Fake.TelegramFailingChats.Add("2000") && app.Fake.TelegramBlockedChats.Add("3000"));

        await outbox.TryEnqueueAsync(new OutboxMessage("test|1", "besked", app.Time.GetUtcNow()), default);
        await dispatcher.DispatchAsync(default);
        await dispatcher.DispatchAsync(default);

        // 1000 got it once; 2000 is retried; 3000 blocked the bot and is not retried.
        var pending = Assert.Single(await outbox.ListPendingAsync(default));
        Assert.Equal(["1000"], pending.DeliveredTo);
        Assert.Equal(["3000"], pending.RejectedBy);
        Assert.Equal("Telegram answered 500.", pending.LastError);

        app.Fake.Locked(() => { app.Fake.TelegramFailingChats.Clear(); return true; });
        await dispatcher.DispatchAsync(default);
        await dispatcher.DispatchAsync(default);

        var sent = app.Fake.Locked(() => app.Fake.TelegramMessages.Select(m => m.ChatId).ToList());
        Assert.Equal(["1000", "2000"], sent);
        Assert.Empty(await outbox.ListPendingAsync(default));
    }

    [Fact]
    public async Task ConcurrentDispatches_SendEachMessageOnce()
    {
        using var app = await StartAsync();
        var dispatcher = app.Get<OutboxDispatcher>();
        for (var i = 0; i < 5; i++)
        {
            await app.Get<IOutboxStore>().TryEnqueueAsync(new OutboxMessage($"test|{i}", $"besked {i}", app.Time.GetUtcNow()), default);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => dispatcher.DispatchAsync(default))));

        Assert.Equal(5, app.Telegrams.Count);
    }

    [Fact]
    public async Task Scheduler_RunsEachSlotOnce_InLocalTime_AndSendsOneDailyReport()
    {
        using var app = await StartAsync();
        var scheduler = app.Get<SchedulerService>();
        var runs = app.Get<ISyncRunStore>();

        await scheduler.TickAsync(default); // 08:05: the 08:00 sync is due
        await scheduler.TickAsync(default);
        Assert.Single(await runs.GetRecentAsync(EloverblikMeasurementSource.Id, 10, default));
        Assert.Empty(app.Telegrams);

        app.Time.Advance(TimeSpan.FromMinutes(56)); // 09:01
        await scheduler.TickAsync(default);
        await scheduler.TickAsync(default);
        var report = Assert.Single(app.Telegrams);
        Assert.StartsWith("Energy Monitor · 8. november", report);
        Assert.Contains("Sommerhus: Seneste komplette døgn 6/11:", report);
        Assert.Contains("Hjem: Seneste komplette døgn 6/11:", report);
        Assert.Contains("Bemærk: Data for 7/11 er endnu ikke komplette", report);
        Assert.Contains("Seneste vellykkede datakontakt: 8/11 kl. 08:05.", report);
        Assert.DoesNotContain("7/11:", report);

        app.Time.Advance(TimeSpan.FromHours(8)); // 17:01
        await scheduler.TickAsync(default);
        await scheduler.TickAsync(default);
        Assert.Equal(2, (await runs.GetRecentAsync(EloverblikMeasurementSource.Id, 10, default)).Count);
        Assert.Single(app.Telegrams);
    }

    [Fact]
    public async Task Scheduler_AfterDowntime_CatchesUpInOrder_SyncBeforeReport()
    {
        using var app = new AppFactory(time: new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 9, 30, 0, TimeSpan.Zero))); // 10:30 local
        await app.SeedPropertiesAsync();

        await app.Get<SchedulerService>().TickAsync(default);

        Assert.Single(await app.Get<ISyncRunStore>().GetRecentAsync(EloverblikMeasurementSource.Id, 10, default));
        Assert.Contains("Seneste komplette døgn 6/11", Assert.Single(app.Telegrams));
    }

    [Fact]
    public async Task Restart_LosesNoData_AndRepeatsNoSlotAlarmOrReport()
    {
        var backend = new InMemoryTableBackend();
        var fake = new FakeState();
        var time = new FakeTimeProvider(AppFactory.Start);
        fake.DayFactors[(FakeState.Sommerhus, Latest)] = 0.15;

        using (var first = new AppFactory(backend, fake, time))
        {
            await first.SeedPropertiesAsync();
            await first.Get<SchedulerService>().TickAsync(default);
            time.Advance(TimeSpan.FromMinutes(56));
            await first.Get<SchedulerService>().TickAsync(default);
            Assert.Equal(2, first.Telegrams.Count); // one alarm, one report
        }

        using var second = new AppFactory(backend, fake, time);
        await second.Get<SchedulerService>().TickAsync(default);
        await second.SyncAsync();

        Assert.Equal(2, second.Telegrams.Count);
        Assert.Single(await second.EventsAsync());
        Assert.Equal(89, (await DaysAsync(second)).Count(d => d.IsComplete));
        Assert.Equal(2, (await second.Get<ISyncRunStore>().GetRecentAsync(EloverblikMeasurementSource.Id, 10, default)).Count);
    }

    [Fact]
    public async Task PausedProperty_StillSyncs_ButRaisesNoConsumptionAlarm()
    {
        using var app = await StartAsync(fake => fake.DayFactors[(FakeState.Sommerhus, Latest)] = 0.15);
        var store = app.Get<IPropertyStore>();
        await store.UpsertAsync((await store.GetAsync("sommerhus", default))! with { MonitoringEnabled = false }, default);

        await app.SyncAsync();

        Assert.Equal(89, (await DaysAsync(app)).Count(d => d.IsComplete));
        Assert.Empty(await app.EventsAsync());
    }

    [Fact]
    public async Task NothingSecret_ReachesTheLogs()
    {
        using var app = await StartAsync();
        await app.SyncAsync();
        app.Fake.ApplyScenario("low");
        await app.SyncAsync();
        app.Fake.ApplyScenario("telegram-down");
        app.Fake.DayFactors[(FakeState.Hjem, Latest)] = 3;
        await app.SyncAsync();
        app.Fake.ApplyScenario("outage");
        await app.SyncAsync();
        using var client = app.ClientFor(AppFactory.Admin);
        await client.PostAsync("/api/admin/notifications/test", null);
        await client.GetAsync("/api/admin/metering-points");

        var logs = app.Logs.Text;
        Assert.Contains("Sync finished", logs);
        Assert.Contains("…0028", logs); // the masked form is what gets logged
        Assert.DoesNotContain(AppFactory.RefreshToken, logs);
        Assert.DoesNotContain(AppFactory.BotToken, logs);
        Assert.DoesNotContain("fake-access", logs);
        Assert.DoesNotContain(FakeState.Hjem, logs);
        Assert.DoesNotContain(FakeState.Sommerhus, logs);
    }

    [Fact]
    public void LocalDay_At_HandlesTheSkippedSpringHour()
    {
        var at = LocalDay.At(new DateOnly(2026, 3, 29), new TimeOnly(2, 30), TestData.Zone);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), at);
    }
}
