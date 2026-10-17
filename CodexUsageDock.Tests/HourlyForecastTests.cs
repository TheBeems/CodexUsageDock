using System.Text.Json;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class HourlyForecastTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PartialDaysAcrossEarlyResetsAndRestartCanBootstrapWithoutACompleteWeek()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("forecast.json");
        var store = new AdaptiveWeeklyUsageStore(path);
        for (var day = 3; day >= 1; day--)
        {
            var start = Now.AddDays(-day);
            var reset = start.AddDays(7);
            var samples = WeeklyForecastTests.Series(start, start.AddHours(4), 100, 96)
                .Select(sample => sample with { ResetsAt = reset, WindowMinutes = 10080 }).ToArray();
            store.Record(new(4, 10080, reset), samples, TimeSpan.FromMinutes(15));
        }

        var restarted = new AdaptiveWeeklyUsageStore(path);
        var fresh = new UsageHistoryEntry(Now, 100, Now.AddDays(7), 10080);
        restarted.Record(new(0, 10080, fresh.ResetsAt!.Value), [fresh], TimeSpan.FromMinutes(15));
        var analysis = UsageTrendAnalyzer.Analyze([fresh], Now, Now.AddDays(7), Now, true,
            TimeSpan.FromMinutes(5), true, restarted.Snapshot);

        Assert.NotNull(analysis.Forecast);
        Assert.Contains("historical usage only", analysis.ForecastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void IntervalsSplitAtUtcHoursAndSurviveRepeatedSerializationExactly()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("hours.json");
        var start = Now.AddMinutes(59).AddSeconds(17).AddTicks(3).ToOffset(TimeSpan.FromHours(2));
        var end = start.AddMinutes(2).AddTicks(5);
        var reset = start.AddDays(7);
        UsageHistoryEntry[] samples = [new(start, 90, reset, 10080), new(end, 88, reset, 10080)];
        var store = new AdaptiveWeeklyUsageStore(path);
        store.Record(new(12, 10080, reset), samples, TimeSpan.FromMinutes(15));

        Assert.Equal(2, store.Snapshot.Hourly!.Buckets.Length);
        Assert.Equal((end - start).Ticks, Ticks(store));
        Assert.Equal(2, Consumed(store), precision: 10);
        Assert.All(store.Snapshot.Hourly.Buckets, bucket =>
        {
            Assert.Equal(TimeSpan.Zero, bucket.StartsAt.Offset);
            Assert.Equal(0, bucket.StartsAt.Ticks % TimeSpan.TicksPerHour);
        });

        for (var restart = 0; restart < 5; restart++)
        {
            store = new AdaptiveWeeklyUsageStore(path);
            store.Record(new(12, 10080, reset), samples, TimeSpan.FromMinutes(15));
            Assert.Equal((end - start).Ticks, Ticks(store));
            Assert.Equal(2, Consumed(store), precision: 10);
        }

        store.Record(new(13, 10080, reset), [new(end.AddMinutes(5), 87, reset, 10080)], TimeSpan.FromMinutes(15));
        Assert.Equal((end.AddMinutes(5) - start).Ticks, Ticks(store));
        Assert.Equal(3, Consumed(store), precision: 10);
    }

    [Theory]
    [InlineData(0)] // Scheduled reset.
    [InlineData(1)] // Early reset.
    [InlineData(2)] // Allowance restoration within the same deadline.
    [InlineData(3)] // A changed deadline without an allowance increase.
    public void ResetsKeepEarlierHoursAndExcludeOnlyTheTransition(int kind)
    {
        using var environment = new TestEnvironment();
        var store = Train(environment.PathFor("reset.json"));
        var oldReset = kind == 0 ? Now : Now.AddDays(2);
        var newReset = kind == 2 ? oldReset : Now.AddDays(7);
        var remaining = kind == 3 ? 95 : 100;
        store.Record(new(5, 10080, oldReset), [new(Now.AddMinutes(-5), 95, oldReset, 10080)], TimeSpan.FromMinutes(15));
        var before = Ticks(store);
        var consumed = Consumed(store);
        var fresh = new UsageHistoryEntry(Now, remaining, newReset, 10080);
        store.Record(new(100 - remaining, 10080, newReset), [fresh], TimeSpan.FromMinutes(15));

        Assert.Equal(before, Ticks(store));
        Assert.Equal(consumed, Consumed(store), precision: 10);
        Assert.NotNull(Analyze(store, fresh).Forecast);

        var restarted = new AdaptiveWeeklyUsageStore(environment.PathFor("reset.json"));
        restarted.Record(new(101 - remaining, 10080, newReset),
            [new(Now.AddMinutes(5), remaining - 1, newReset, 10080)], TimeSpan.FromMinutes(15));
        Assert.Equal(before + TimeSpan.FromMinutes(5).Ticks, Ticks(restarted));
        Assert.Equal(consumed + 1, Consumed(restarted), precision: 10);
    }

    [Fact]
    public void MovingUnusedDeadlinesAndFirstConsumptionDoNotDiscardHours()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("moving.json");
        var store = Train(path);
        var before = Ticks(store);
        for (var minute = 0; minute <= 90; minute++)
        {
            var at = Now.AddMinutes(minute);
            store.Record(new(0, 10080, at.AddDays(7)), [new(at, 100, at.AddDays(7), 10080)], TimeSpan.FromMinutes(15));
            if (minute % 10 == 0) store = new AdaptiveWeeklyUsageStore(path);
        }

        var reset = Now.AddMinutes(90).AddDays(7);
        var firstUse = new UsageHistoryEntry(Now.AddMinutes(91), 99, reset, 10080);
        store.Record(new(1, 10080, reset), [firstUse], TimeSpan.FromMinutes(15));
        Assert.Equal(before + TimeSpan.FromMinutes(91).Ticks, Ticks(store));
        Assert.Equal(13, Consumed(store), precision: 8);
        Assert.NotNull(Analyze(store, firstUse).Forecast);
        Assert.Empty(store.Snapshot.CompletedCycles);
    }

    [Fact]
    public void GapsStayUnknownButMeasuredIdleTimeCounts()
    {
        using var environment = new TestEnvironment();
        var store = Train(environment.PathFor("gaps.json"));
        var reset = Now.AddDays(7);
        var before = Ticks(store);
        var consumed = Consumed(store);
        store.Record(new(10, 10080, reset), [new(Now, 90, reset, 10080)], TimeSpan.FromMinutes(15));
        var afterGap = new UsageHistoryEntry(Now.AddHours(8), 80, reset, 10080);
        store.Record(new(20, 10080, reset), [afterGap], TimeSpan.FromMinutes(15));
        Assert.Equal(before, Ticks(store));
        Assert.Equal(consumed, Consumed(store), precision: 8);
        Assert.NotNull(Analyze(store, afterGap).Forecast);

        store.Record(new(20, 10080, reset), Series(Now.AddHours(8), Now.AddHours(9), 80, 80, reset), TimeSpan.FromMinutes(15));
        Assert.Equal(before + TimeSpan.FromHours(1).Ticks, Ticks(store));
        Assert.Equal(consumed, Consumed(store), precision: 8);
    }

    [Fact]
    public void WholeHoursDoNotDisappearThroughFloatingPointRounding()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("precision.json");
        var store = new AdaptiveWeeklyUsageStore(path);
        var start = Now.AddDays(-1).AddMinutes(3).AddTicks(7);
        var reset = start.AddDays(7);
        var samples = Series(start, start.AddHours(10), 100, 90, reset);
        foreach (var sample in samples)
        {
            store.Record(new(100 - sample.RemainingPercent, 10080, reset), [sample], TimeSpan.FromMinutes(15));
            store = new AdaptiveWeeklyUsageStore(path);
        }

        Assert.Equal(TimeSpan.FromHours(10).Ticks, Ticks(store));
        Assert.Equal(10, Consumed(store), precision: 9);
        Assert.All(store.Snapshot.Hourly!.Buckets, bucket => Assert.InRange(bucket.ObservedTicks, 1, TimeSpan.TicksPerHour));
    }

    [Fact]
    public void ColdServiceRestartsUseOneFreshMeasurementAndKeepAccountsSeparate()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("service.json");
        Train(new AdaptiveWeeklyUsageStore(path).ForContext("a|codex"));
        var reset = Now.AddDays(7);
        for (var restart = 0; restart < 3; restart++)
        {
            var at = Now.AddMinutes(restart);
            var snapshot = Snapshot("a", at, 90, reset);
            using var service = environment.CreateService(_ => Task.FromResult(snapshot), () => snapshot,
                adaptiveWeeklyUsageStore: new AdaptiveWeeklyUsageStore(path), clock: () => at);
            service.RecordHistory(snapshot, at);
            Assert.Equal(TimeSpan.FromHours(12).Ticks, service.AdaptiveWeeklyHistory.Hourly!.Buckets.Sum(bucket => bucket.ObservedTicks));
            Assert.NotNull(UsageTrendAnalyzer.Analyze(service.WeeklyHistory, reset.AddDays(-7), reset, at,
                true, TimeSpan.FromMinutes(5), true, service.AdaptiveWeeklyHistory).Forecast);

            service.RecordHistory(Snapshot("b", at, 90, reset), at);
            Assert.Empty(service.AdaptiveWeeklyHistory.Hourly!.Buckets);
            service.RecordHistory(snapshot, at);
            Assert.Equal(TimeSpan.FromHours(12).Ticks, service.AdaptiveWeeklyHistory.Hourly!.Buckets.Sum(bucket => bucket.ObservedTicks));
        }
    }

    [Fact]
    public void PausingAcrossServiceRestartsDoesNotReplayRawMeasurements()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("paused.json");
        Train(new AdaptiveWeeklyUsageStore(path).ForContext("a|codex"));
        var reset = Now.AddDays(7);
        var snapshot = Snapshot("a", Now, 90, reset);
        using (var paused = environment.CreateService(_ => Task.FromResult(snapshot), () => snapshot,
            adaptiveWeeklyUsageStore: new AdaptiveWeeklyUsageStore(path)))
        {
            paused.SetAdaptiveWeeklyForecastEnabled(false);
            paused.RecordHistory(snapshot, Now);
            paused.RecordHistory(Snapshot("a", Now.AddHours(1), 80, reset), Now.AddHours(1));
        }

        using var resumed = environment.CreateService(_ => Task.FromResult(snapshot), () => snapshot,
            adaptiveWeeklyUsageStore: new AdaptiveWeeklyUsageStore(path));
        resumed.SetAdaptiveWeeklyForecastEnabled(false);
        resumed.RecordHistory(Snapshot("a", Now.AddHours(2), 70, reset), Now.AddHours(2));
        resumed.SetAdaptiveWeeklyForecastEnabled(true);
        resumed.RecordHistory(Snapshot("a", Now.AddHours(2).AddMinutes(5), 60, reset), Now.AddHours(2).AddMinutes(5));
        resumed.RecordHistory(Snapshot("a", Now.AddHours(2).AddMinutes(10), 59, reset), Now.AddHours(2).AddMinutes(10));
        Assert.Equal(TimeSpan.FromHours(12).Add(TimeSpan.FromMinutes(5)).Ticks,
            resumed.AdaptiveWeeklyHistory.Hourly!.Buckets.Sum(bucket => bucket.ObservedTicks));
        Assert.Equal(13, resumed.AdaptiveWeeklyHistory.Hourly.Buckets.Sum(bucket => bucket.ConsumedPercent), precision: 8);
    }

    [Fact]
    public void LegacyLearnedSummariesRemainUsableAndTheOriginalFileIsUnchanged()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("legacy.json");
        var reset = Now.AddDays(4);
        var legacy = new AdaptiveWeeklyUsageState([], new(reset, 10080, 720, 12,
            [new(0, 240, 4), new(4, 240, 4), new(8, 240, 4)]),
            new(Now.AddMinutes(-5), 80, reset, 10080), true);
        var original = JsonSerializer.Serialize(legacy, AdaptiveWeeklyUsageJsonContext.Default.AdaptiveWeeklyUsageState);
        File.WriteAllText(path, original);
        var store = new AdaptiveWeeklyUsageStore(path);
        var fresh = new UsageHistoryEntry(Now, 80, reset, 10080);
        store.AdvanceBaseline(new(20, 10080, reset), [fresh]);

        Assert.NotNull(Analyze(store, fresh).Forecast);
        Assert.Contains("preserved learned history", Analyze(store, fresh).ForecastStatus, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.NotEqual(path, store.StoragePath);
        Assert.Empty(store.Snapshot.Hourly!.Buckets);

        Assert.True(store.Clear());
        var restarted = new AdaptiveWeeklyUsageStore(path);
        restarted.Record(new(20, 10080, reset), Series(Now.AddHours(-4), Now, 90, 80, reset), TimeSpan.FromMinutes(15));
        Assert.Empty(restarted.Snapshot.Hourly!.Buckets);
        Assert.Null(Analyze(restarted, fresh).Forecast);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void ClearingHoursDoesNotRelearnChartSamplesAfterRestart()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("clear.json");
        var store = Train(path);
        Assert.True(store.Clear());
        var restarted = new AdaptiveWeeklyUsageStore(path);
        var reset = Now.AddDays(7);
        restarted.Record(new(12, 10080, reset), Series(Now, Now.AddHours(4), 100, 88, reset), TimeSpan.FromMinutes(15));
        Assert.Empty(restarted.Snapshot.Hourly!.Buckets);
        Assert.Null(restarted.Snapshot.ActiveCycle);
        restarted.Record(new(13, 10080, reset), [new(Now.AddHours(4).AddMinutes(5), 87, reset, 10080)], TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, Ticks(restarted));
    }

    [Fact]
    public void SaveAndClearFailuresPreserveSavedHoursAndCanBeRetried()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("locked.json");
        var store = Train(path);
        var saved = File.ReadAllText(store.StoragePath);
        var reset = Now.AddDays(7);
        using (var held = new FileStream(store.StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.Clear());
            Assert.Equal(TimeSpan.FromHours(12).Ticks, Ticks(store));
            store.Record(new(10, 10080, reset), [new(Now, 90, reset, 10080)], TimeSpan.FromMinutes(15));
            Assert.NotNull(store.StorageError);
            Assert.Equal(saved, File.ReadAllText(store.StoragePath));
        }

        store.Record(new(10, 10080, reset), [new(Now, 90, reset, 10080)], TimeSpan.FromMinutes(15));
        Assert.Null(store.StorageError);
        Assert.Equal(TimeSpan.FromHours(12).Ticks, Ticks(new AdaptiveWeeklyUsageStore(path)));
        Assert.True(store.Clear());
        Assert.Empty(new AdaptiveWeeklyUsageStore(path).Snapshot.Hourly!.Buckets);
    }

    [Fact]
    public void UnreadableHourlyFileIsNotOverwrittenOrReplacedWithLegacyData()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("invalid.json");
        var store = Train(path);
        File.WriteAllText(store.StoragePath, "{invalid saved history");
        var restarted = new AdaptiveWeeklyUsageStore(path);
        var reset = Now.AddDays(7);
        restarted.Record(new(10, 10080, reset), [new(Now, 90, reset, 10080)], TimeSpan.FromMinutes(15));
        Assert.Equal("{invalid saved history", File.ReadAllText(store.StoragePath));
        Assert.NotNull(restarted.StorageError);
        Assert.Empty(restarted.Snapshot.Hourly!.Buckets);
        Assert.True(restarted.Clear());
        Assert.Null(new AdaptiveWeeklyUsageStore(path).StorageError);
    }

    [Fact]
    public void HourRetentionDependsOnElapsedTimeRatherThanResetCount()
    {
        using var environment = new TestEnvironment();
        var store = new AdaptiveWeeklyUsageStore(environment.PathFor("retention.json"));
        for (var day = 60; day >= 0; day--)
        {
            var start = Now.AddDays(-day);
            var reset = start.AddDays(7);
            store.Record(new(1, 10080, reset), Series(start, start.AddHours(1), 100, 99, reset), TimeSpan.FromMinutes(15));
        }

        Assert.Equal(56, store.Snapshot.Hourly!.Buckets.Length);
        Assert.Equal(Now.AddDays(-55), store.Snapshot.Hourly.Buckets[0].StartsAt);
        Assert.Equal(TimeSpan.FromHours(56).Ticks, Ticks(store));
    }

    [Fact]
    public void StoredHistoryDoesNotOverrideEstablishedRecentPace()
    {
        using var environment = new TestEnvironment();
        var store = Train(environment.PathFor("recent.json"));
        var reset = Now.AddDays(7);
        var samples = Series(Now, Now.AddHours(1), 90, 80, reset);
        store.Record(new(20, 10080, reset), samples, TimeSpan.FromMinutes(15));
        var current = samples[^1];
        var withHistory = UsageTrendAnalyzer.Analyze(samples, reset.AddDays(-7), reset, current.RecordedAt,
            true, TimeSpan.FromMinutes(5), true, store.Snapshot);
        var recentOnly = UsageTrendAnalyzer.Analyze(samples, reset.AddDays(-7), reset, current.RecordedAt,
            true, TimeSpan.FromMinutes(5));
        Assert.NotNull(withHistory.Forecast);
        Assert.Equal(recentOnly.Forecast!.EndsAt, withHistory.Forecast.EndsAt);
        Assert.Equal(recentOnly.Forecast.RemainingPercent, withHistory.Forecast.RemainingPercent);
        Assert.Contains("available for startup", withHistory.ForecastStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 6)]
    public void HistoricalStartupStillRequiresEnabledForecastingAndFreshData(bool enabled, int ageMinutes)
    {
        using var environment = new TestEnvironment();
        var store = Train(environment.PathFor("fresh.json"));
        var sample = new UsageHistoryEntry(Now.AddMinutes(-ageMinutes), 90, Now.AddDays(6), 10080);
        Assert.Null(UsageTrendAnalyzer.Analyze([sample], Now.AddDays(-1), Now.AddDays(6), Now, true,
            TimeSpan.FromMinutes(5), enabled, store.Snapshot).Forecast);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(12, 1)]
    [InlineData(6, 2)]
    public void ShortOrSingleDayHistoryDoesNotQualifyAsAStartupBaseline(int hoursPerDay, int days)
    {
        using var environment = new TestEnvironment();
        var store = new AdaptiveWeeklyUsageStore(environment.PathFor("short.json"));
        for (var day = days; day > 0; day--)
        {
            var start = Now.AddDays(-day);
            var reset = start.AddDays(7);
            store.Record(new(1, 10080, reset), Series(start, start.AddHours(hoursPerDay), 100, 99, reset), TimeSpan.FromMinutes(15));
        }

        Assert.Null(Analyze(store, new(Now, 90, Now.AddDays(7), 10080)).Forecast);
    }

    [Fact]
    public void FutureAndExpiredHistoryDoNotProvideHistoricalStartup()
    {
        using var environment = new TestEnvironment();
        var store = Train(environment.PathFor("age.json"));
        var future = store.Snapshot with
        {
            Hourly = store.Snapshot.Hourly! with { RecordedThrough = Now.AddDays(1) },
        };
        Assert.Null(UsageTrendAnalyzer.Analyze([new(Now, 90, Now.AddDays(7), 10080)], Now, Now.AddDays(7), Now,
            true, TimeSpan.FromMinutes(5), true, future).Forecast);

        var later = Now.AddDays(60);
        var fresh = new UsageHistoryEntry(later, 90, later.AddDays(7), 10080);
        Assert.Null(Analyze(store, fresh).Forecast);
        store.AdvanceBaseline(new(10, 10080, fresh.ResetsAt!.Value), [fresh]);
        Assert.Empty(store.Snapshot.Hourly!.Buckets);
    }

    [Fact]
    public void AChangedWindowDurationBreaksLearningWithoutDeletingEarlierHours()
    {
        using var environment = new TestEnvironment();
        var store = Train(environment.PathFor("window.json"));
        var reset = Now.AddDays(7);
        store.Record(new(10, 10080, reset), [new(Now, 90, reset, 10080)], TimeSpan.FromMinutes(15));
        store.Record(new(15, 300, Now.AddHours(5)), [new(Now.AddMinutes(1), 85, Now.AddHours(5), 300)], TimeSpan.FromMinutes(15));
        store = new AdaptiveWeeklyUsageStore(environment.PathFor("window.json"));
        Assert.Null(store.StorageError);
        store.Record(new(20, 10080, reset), [new(Now.AddMinutes(2), 80, reset, 10080)], TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromHours(12).Ticks, Ticks(store));
        Assert.Equal(12, Consumed(store), precision: 8);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OffsetChangesDoNotMergeOrSkipElapsedHours(bool fallBack)
    {
        using var environment = new TestEnvironment();
        var store = new AdaptiveWeeklyUsageStore(environment.PathFor("offset.json"));
        var start = fallBack
            ? new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(2))
            : new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.FromHours(1));
        var changedOffset = fallBack ? TimeSpan.FromHours(1) : TimeSpan.FromHours(2);
        var reset = start.AddDays(7);
        var samples = Series(start, start.AddHours(2), 100, 98, reset)
            .Select(sample => sample.RecordedAt >= start.AddHours(1)
                ? sample with { RecordedAt = sample.RecordedAt.ToOffset(changedOffset) } : sample).ToArray();
        store.Record(new(2, 10080, reset), samples, TimeSpan.FromMinutes(15));
        Assert.Equal(2, store.Snapshot.Hourly!.Buckets.Length);
        Assert.Equal(TimeSpan.FromHours(2).Ticks, Ticks(store));
        Assert.Equal(2, Consumed(store), precision: 8);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("duration")]
    [InlineData("future")]
    public void InvalidHourlyDocumentsArePreservedRatherThanSilentlyRelearned(string invalidity)
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("schema.json");
        var store = Train(path);
        var state = JsonSerializer.Deserialize(File.ReadAllText(store.StoragePath), HourlyUsageJsonContext.Default.HourlyUsageState)!;
        var first = state.Buckets[0];
        state = invalidity switch
        {
            "version" => state with { Version = 2 },
            "duplicate" => state with { Buckets = [first, first] },
            "duration" => state with { Buckets = [first with { ObservedTicks = TimeSpan.TicksPerHour + 1 }] },
            _ => state with { Buckets = [first with { StartsAt = Now.AddDays(1) }] },
        };
        var original = JsonSerializer.Serialize(state, HourlyUsageJsonContext.Default.HourlyUsageState);
        File.WriteAllText(store.StoragePath, original);
        var restored = new AdaptiveWeeklyUsageStore(path);
        restored.AdvanceBaseline(new(10, 10080, Now.AddDays(7)), [new(Now, 90, Now.AddDays(7), 10080)]);
        Assert.NotNull(restored.StorageError);
        Assert.Empty(restored.Snapshot.Hourly!.Buckets);
        Assert.Equal(original, File.ReadAllText(store.StoragePath));
    }

    private static AdaptiveWeeklyUsageStore Train(string path) => Train(new AdaptiveWeeklyUsageStore(path));

    private static AdaptiveWeeklyUsageStore Train(AdaptiveWeeklyUsageStore store)
    {
        for (var day = 3; day >= 1; day--)
        {
            var start = Now.AddDays(-day);
            var reset = start.AddDays(7);
            store.Record(new(4, 10080, reset), Series(start, start.AddHours(4), 100, 96, reset), TimeSpan.FromMinutes(15));
        }

        return store;
    }

    private static UsageHistoryEntry[] Series(DateTimeOffset start, DateTimeOffset end, double first, double last, DateTimeOffset reset) =>
        WeeklyForecastTests.Series(start, end, first, last)
            .Select(sample => sample with { ResetsAt = reset, WindowMinutes = 10080 }).ToArray();

    private static long Ticks(AdaptiveWeeklyUsageStore store) => store.Snapshot.Hourly!.Buckets.Sum(bucket => bucket.ObservedTicks);

    private static double Consumed(AdaptiveWeeklyUsageStore store) => store.Snapshot.Hourly!.Buckets.Sum(bucket => bucket.ConsumedPercent);

    private static UsageTrendAnalyzer.TrendAnalysis Analyze(AdaptiveWeeklyUsageStore store, UsageHistoryEntry sample) =>
        UsageTrendAnalyzer.Analyze([sample], sample.ResetsAt!.Value.AddDays(-7), sample.ResetsAt, sample.RecordedAt,
            true, TimeSpan.FromMinutes(5), true, store.Snapshot);

    private static CodexUsageSnapshot Snapshot(string account, DateTimeOffset at, double remaining, DateTimeOffset reset) =>
        new(null, new(100 - remaining, 10080, reset), null, null, null, at, UsageDataSource.AppServer, null,
            AccountKey: account, DefaultBucketId: "codex");
}
