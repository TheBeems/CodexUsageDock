using System.Text.Json;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class ReliabilityTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    private static void SubmitSettings(CodexUsageDockSettingsPage page, string interval = "15")
    {
        var form = page.GetContent().OfType<FormContent>().Last();
        form.SubmitForm("""
            {"showFiveHourLimit":"false","showWeeklyLimit":"true","showResetsAndCredits":"false",
            "showResetTime":"false","useAdaptiveWeeklyForecast":"false","refreshInterval":"INTERVAL"}
            """.Replace("INTERVAL", interval, StringComparison.Ordinal), "{}");
    }

    [Fact]
    public void SettingsPersistAcrossNewPages()
    {
        var first = _environment.CreateSettings();
        SubmitSettings(first);
        Assert.False(first.ShowFiveHourLimit);
        Assert.Null(first.StatusMessage);
        var restarted = _environment.CreateSettings();
        Assert.False(restarted.ShowFiveHourLimit);
        Assert.True(restarted.ShowWeeklyLimit);
        Assert.False(restarted.ShowResetsAndCredits);
        Assert.False(restarted.ShowResetTime);
        Assert.False(restarted.UseAdaptiveWeeklyForecast);
        Assert.Equal(TimeSpan.FromMinutes(15), restarted.RefreshInterval);
    }

    [Fact]
    public void SettingsReportFailedSaveAndSupportRetry()
    {
        var settings = _environment.CreateSettings();
        SubmitSettings(settings, "5");
        var path = _environment.PathFor("settings.json");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            SubmitSettings(settings);
            Assert.Equal(TimeSpan.FromMinutes(15), settings.RefreshInterval);
            Assert.Contains("could not be saved", settings.StatusMessage, StringComparison.Ordinal);
            Assert.Equal(TimeSpan.FromMinutes(5), _environment.CreateSettings().RefreshInterval);
        }

        SubmitSettings(settings);
        Assert.Null(settings.StatusMessage);
        Assert.Equal(TimeSpan.FromMinutes(15), _environment.CreateSettings().RefreshInterval);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("not json")]
    public void SettingsRecoverFromInvalidDocuments(string json)
    {
        File.WriteAllText(_environment.PathFor("settings.json"), json);
        var settings = _environment.CreateSettings();
        Assert.True(settings.ShowFiveHourLimit);
        Assert.Equal(TimeSpan.FromMinutes(1), settings.RefreshInterval);
        Assert.NotNull(settings.StatusMessage);
    }

    [Fact]
    public void SettingsIgnoreInvalidFieldsAndPreserveValidChoices()
    {
        File.WriteAllText(_environment.PathFor("settings.json"), """
            {"showFiveHourLimit":"false","showWeeklyLimit":null,"showResetTime":"invalid","refreshInterval":"999","unknown":"value"}
            """);
        var settings = _environment.CreateSettings();
        Assert.False(settings.ShowFiveHourLimit);
        Assert.True(settings.ShowWeeklyLimit);
        Assert.True(settings.ShowResetTime);
        Assert.Equal(TimeSpan.FromMinutes(1), settings.RefreshInterval);
    }

    [Fact]
    public void ProviderAppliesSavedRefreshIntervalBeforeFirstRead()
    {
        SubmitSettings(_environment.CreateSettings());
        using var service = _environment.CreateService();
        using var provider = new CodexUsageDockCommandsProvider(service, _environment.CreateSettings());
        Assert.Equal(TimeSpan.FromMinutes(15), service.RefreshInterval);
        var band = Assert.Single(provider.GetDockBands()!);
        var items = Assert.IsAssignableFrom<Microsoft.CommandPalette.Extensions.IListPage>(band.Command).GetItems();
        Assert.Single(items);
        Assert.StartsWith("Week", items[0].Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(90, 0, "Weekly window")]
    [InlineData(0, 90, "5-hour window")]
    public void SummaryWarnsAboutTheExhaustedWindow(int fiveHourRemaining, int weeklyRemaining, string expectedWindow)
    {
        var snapshot = Snapshot(weeklyRemaining) with { Primary = new RateLimitWindow(100 - fiveHourRemaining, 300, Now.AddHours(4)) };
        var summary = CodexUsageDockPage.FormatSummary(snapshot, Now);
        Assert.Contains("Almost at your limit", summary, StringComparison.Ordinal);
        Assert.Contains(expectedWindow, summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Plenty", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackUsesMeasurementTimestampAndClearsInactiveWindows()
    {
        var home = _environment.PathFor("codex");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var file = Path.Combine(sessions, "rollout-one.jsonl");
        File.WriteAllLines(file, [QuotaLine(Now.AddHours(-5), 80), QuotaLine(Now.AddHours(-4), null), "{\"payload\":{\"type\":\"message\"}}"]);
        File.SetLastWriteTimeUtc(file, Now.UtcDateTime);
        var result = LocalCodexSessionReader.ReadLatest(home, Now);
        Assert.Equal(Now.AddHours(-4), result.UpdatedAt);
        Assert.Null(result.Primary);
        Assert.NotNull(result.Secondary);
        Assert.Equal("Fallback · 4 hours old", UsageDockItem.FormatFallbackAge(result, Now));
    }

    [Fact]
    public void FallbackFindsLatestMeasurementAcrossFilesAndIgnoresUnreadableAndMalformedFiles()
    {
        var home = _environment.PathFor("codex");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        for (var index = 0; index < 13; index++)
        {
            File.WriteAllText(Path.Combine(sessions, $"rollout-noise-{index}.jsonl"), "[\"rate_limits\"]\n");
        }

        var oldFile = Path.Combine(sessions, "rollout-old-file.jsonl");
        File.WriteAllLines(oldFile, [QuotaLine(Now.AddMinutes(-5), 20), QuotaLine(Now.AddMinutes(-10), 30), QuotaLine(Now.AddHours(1), 90)]);
        File.SetLastWriteTimeUtc(oldFile, Now.AddDays(-1).UtcDateTime);
        var lockedPath = Path.Combine(sessions, "rollout-locked.jsonl");
        File.WriteAllText(lockedPath, QuotaLine(Now, 99));
        using var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = LocalCodexSessionReader.ReadLatest(home, Now);
        Assert.Equal(Now.AddMinutes(-5), result.UpdatedAt);
        Assert.Equal(20, result.Primary!.UsedPercent);
    }

    [Fact]
    public void InvalidHistoryEntriesDoNotPreventStartup()
    {
        var weeklyPath = _environment.PathFor("weekly.json");
        File.WriteAllText(weeklyPath, "[null,{}]");
        using var service = _environment.CreateService();
        Assert.Empty(service.WeeklyHistory);
    }

    [Fact]
    public void AdaptiveHistoryIgnoresNullBucketsAndImpossibleCycles()
    {
        var path = _environment.PathFor("adaptive.json");
        var state = new AdaptiveWeeklyUsageState([null!, new(DateTimeOffset.MinValue, 10080, 60, 10, [])],
            new AdaptiveWeeklyUsageCycle(Now.AddDays(3), 10080, 60, 10,
                [null!, new(0, 60, 10), new(1, 361, 10), new(2, 10, double.MaxValue), new(2, 10, double.MaxValue)]),
            null, true);
        File.WriteAllText(path, JsonSerializer.Serialize(state, AdaptiveWeeklyUsageJsonContext.Default.AdaptiveWeeklyUsageState));
        var store = new AdaptiveWeeklyUsageStore(path);
        Assert.Empty(store.Snapshot.CompletedCycles);
        Assert.Single(store.Snapshot.ActiveCycle!.Buckets);
        Assert.True(store.Clear());
    }

    [Fact]
    public void InjectedHistoryStoresRemainIsolated()
    {
        using var other = new TestEnvironment();
        using var first = _environment.CreateService();
        using var second = other.CreateService();
        first.RecordHistory(Snapshot(80) with { AccountKey = "test-account" }, Now);
        Assert.Single(first.WeeklyHistory);
        Assert.Empty(second.WeeklyHistory);
        Assert.Single(new WeeklyUsageHistoryStore(_environment.PathFor("weekly.json")).ForContext("test-account|codex").Load(Now));
        Assert.False(File.Exists(other.PathFor("weekly.json")));
        Assert.False(File.Exists(other.PathFor("adaptive.json")));
    }

    [Fact]
    public void WeeklyHistoryReportsFailedSaveAndRetries()
    {
        var path = _environment.PathFor("weekly.json");
        var store = new WeeklyUsageHistoryStore(path);
        Assert.True(store.Save([new(Now.AddMinutes(-1), 90)]));
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.Save([new(Now, 80)]));
            Assert.NotNull(store.StorageError);
            Assert.Equal(90, Assert.Single(new WeeklyUsageHistoryStore(path).Load(Now)).RemainingPercent);
        }

        Assert.True(store.Save([new(Now, 80)]));
        Assert.Null(store.StorageError);
        Assert.Equal(80, Assert.Single(new WeeklyUsageHistoryStore(path).Load(Now)).RemainingPercent);
    }

    [Fact]
    public void ClearReportsFailureWithoutDiscardingHistoryAndCanBeRetried()
    {
        var path = _environment.PathFor("adaptive.json");
        var state = new AdaptiveWeeklyUsageState([], new(Now.AddDays(3), 10080, 60, 10, []), null, true);
        File.WriteAllText(path, JsonSerializer.Serialize(state, AdaptiveWeeklyUsageJsonContext.Default.AdaptiveWeeklyUsageState));
        var store = new AdaptiveWeeklyUsageStore(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.Clear());
            Assert.NotNull(store.Snapshot.ActiveCycle);
            Assert.NotNull(store.StorageError);
        }

        Assert.True(store.Clear());
        Assert.Null(store.StorageError);
        Assert.Null(new AdaptiveWeeklyUsageStore(path).Snapshot.ActiveCycle);
    }

    [Fact]
    public void TextAndChartBothPauseForecastAfterMeasurementGap()
    {
        var snapshot = Snapshot(70);
        UsageHistoryEntry[] history = [new(Now.AddDays(-1), 100), new(Now.AddDays(-1).AddMinutes(5), 99), new(Now, 70)];
        using var json = JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(snapshot, Now, false, [], history, TimeSpan.FromMinutes(1), false));
        Assert.Equal("Projection will appear after another measurement.", json.RootElement.GetProperty("weeklyProjection").GetString());
        Assert.Contains("Forecast is unavailable", json.RootElement.GetProperty("weeklyTrendChartAlt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForecastUsesOnlyContinuousMeasurementsAfterGap()
    {
        UsageHistoryEntry[] history = [new(Now.AddDays(-1), 100), new(Now.AddMinutes(-5), 75), new(Now, 70)];
        var result = UsageTrendAnalyzer.Analyze(history, Now.AddDays(-4), Now.AddDays(3), Now, true, TimeSpan.FromMinutes(5));
        Assert.Equal(Now.AddMinutes(70), result.Forecast!.EndsAt);
    }

    [Fact]
    public async Task SlowTokenReadDoesNotBlockLimitsOrAllowStaleTokensToPublish()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenResult = new TaskCompletionSource<LocalTokenUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenCalls = 0;
        var snapshot = Snapshot(80);
        using var service = _environment.CreateService(_ => Task.FromResult(snapshot), () => snapshot,
            localTokenUsageReader: (_, _, _, _) =>
            {
                Interlocked.Increment(ref tokenCalls);
                started.TrySetResult();
                return tokenResult.Task;
            });
        await service.RefreshAsync().WaitAsync(TestTimeout);
        await started.Task.WaitAsync(TestTimeout);
        Assert.False(service.IsLoading);
        Assert.Equal(snapshot, service.Current with { LastAttemptAt = null });
        Assert.NotNull(service.Current.LastAttemptAt);
        var originalTokens = service.TokenRefreshTask;
        snapshot = snapshot with { Secondary = snapshot.Secondary! with { ResetsAt = Now.AddDays(6) } };
        await service.RefreshAsync().WaitAsync(TestTimeout);
        Assert.Equal(1, Volatile.Read(ref tokenCalls));
        tokenResult.SetResult(new LocalTokenUsageSnapshot([new(DateOnly.FromDateTime(Now.Date), 123)], Now, LocalTokenUsageStatus.Complete));
        await originalTokens.WaitAsync(TestTimeout);
        Assert.Equal(LocalTokenUsageStatus.Unavailable, service.CurrentTokenUsage.Status);
        await service.RefreshAsync().WaitAsync(TestTimeout);
        await service.TokenRefreshTask.WaitAsync(TestTimeout);
        Assert.Equal(2, Volatile.Read(ref tokenCalls));
        Assert.Equal(LocalTokenUsageStatus.Complete, service.CurrentTokenUsage.Status);
    }

    [Fact]
    public async Task DisposeCancelsIndependentTokenRead()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = _environment.CreateService(_ => Task.FromResult(Snapshot(80)), () => Snapshot(80),
            localTokenUsageReader: async (_, _, _, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return LocalTokenUsageSnapshot.Unavailable;
            });
        await service.RefreshAsync();
        await started.Task.WaitAsync(TestTimeout);
        service.Dispose();
        await service.TokenRefreshTask.WaitAsync(TestTimeout);
    }

    private static CodexUsageSnapshot Snapshot(int remaining) => new(null, new(100 - remaining, 10080, Now.AddDays(3)),
        null, null, null, Now, UsageDataSource.AppServer, null);

    private static string QuotaLine(DateTimeOffset timestamp, int? primaryUsed) => JsonSerializer.Serialize(new
    {
        timestamp,
        payload = new
        {
            rate_limits = new
            {
                primary = primaryUsed.HasValue ? new { used_percent = primaryUsed.Value, window_minutes = 300, resets_at = Now.AddHours(1).ToUnixTimeSeconds() } : null,
                secondary = new { used_percent = 25, window_minutes = 10080, resets_at = Now.AddDays(3).ToUnixTimeSeconds() },
            },
        },
    });
}
