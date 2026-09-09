using Xunit;

namespace CodexUsageDock.Tests;

public sealed class UsageContextTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly TestEnvironment _environment = new();
    public void Dispose() => _environment.Dispose();

    private static CodexUsageSnapshot Snapshot(string? account, double used = 20) => new(
        new(used, 300, Now.AddHours(4)), new(used, 10080, Now.AddDays(6)),
        null, null, null, Now, UsageDataSource.AppServer, null,
        AccountKey: account, DefaultBucketId: "codex");

    [Fact]
    public void AccountAndCategoryHistoryAreSeparatedAndRestoredOnlyAfterIdentification()
    {
        using (var service = _environment.CreateService())
        {
            service.RecordHistory(Snapshot("account-a"), Now);
            service.RecordHistory(Snapshot("account-b", 70), Now);
            Assert.Equal(30, Assert.Single(service.WeeklyHistory).RemainingPercent);
            service.RecordHistory(Snapshot("account-a", 50) with { DefaultBucketId = "review" }, Now);
            Assert.Equal(50, Assert.Single(service.WeeklyHistory).RemainingPercent);
            service.RecordHistory(Snapshot("account-a"), Now);
            Assert.Equal(80, Assert.Single(service.WeeklyHistory).RemainingPercent);
        }

        using var restarted = _environment.CreateService();
        Assert.Empty(restarted.WeeklyHistory);
        restarted.RecordHistory(Snapshot("account-b", 70), Now);
        Assert.Equal(30, Assert.Single(restarted.WeeklyHistory).RemainingPercent);
    }

    [Fact]
    public void UnverifiedAndLegacyHistoryDoNotSeedVerifiedAccounts()
    {
        new WeeklyUsageHistoryStore(_environment.PathFor("weekly.json")).Save([new(Now.AddMinutes(-1), 1)]);
        using var service = _environment.CreateService();
        service.RecordHistory(Snapshot(null), Now);
        Assert.Single(service.WeeklyHistory);
        Assert.Null(service.AdaptiveWeeklyHistory.ActiveCycle);
        service.RecordHistory(Snapshot("known", 50), Now);
        Assert.Equal(50, Assert.Single(service.WeeklyHistory).RemainingPercent);
        Assert.Equal(1, Assert.Single(new WeeklyUsageHistoryStore(_environment.PathFor("weekly.json")).Load(Now)).RemainingPercent);
    }

    [Fact]
    public void UnverifiedCategoriesHaveSeparateInMemoryHistory()
    {
        using var service = _environment.CreateService();
        service.RecordHistory(Snapshot(null), Now);
        service.RecordHistory(Snapshot(null, 90) with { DefaultBucketId = "review", UpdatedAt = Now.AddMinutes(1) }, Now.AddMinutes(1));
        Assert.Equal(10, Assert.Single(service.WeeklyHistory).RemainingPercent);
        Assert.False(File.Exists(_environment.PathFor("weekly.json")));
    }

    [Fact]
    public void SwitchingAccountsAfterReenablingDoesNotReplayPausedObservations()
    {
        using var service = _environment.CreateService();
        service.RecordHistory(Snapshot("a", 0), Now);
        service.RecordHistory(Snapshot("a", 10) with { UpdatedAt = Now.AddMinutes(1) }, Now.AddMinutes(1));
        service.SetAdaptiveWeeklyForecastEnabled(false);
        service.RecordHistory(Snapshot("a", 20) with { UpdatedAt = Now.AddMinutes(2) }, Now.AddMinutes(2));
        service.RecordHistory(Snapshot("b", 50), Now.AddMinutes(2));
        service.SetAdaptiveWeeklyForecastEnabled(true);
        service.RecordHistory(Snapshot("b", 60) with { UpdatedAt = Now.AddMinutes(3) }, Now.AddMinutes(3));
        service.RecordHistory(Snapshot("a", 30) with { UpdatedAt = Now.AddMinutes(4) }, Now.AddMinutes(4));
        Assert.Equal(10, service.AdaptiveWeeklyHistory.ActiveCycle!.ConsumedPercent);
    }

    [Fact]
    public async Task FailureKeepsOriginalConfirmedTimestampAndDoesNotLearnAnotherSample()
    {
        var failed = false;
        using var service = _environment.CreateService(
            _ => failed ? Task.FromException<CodexUsageSnapshot>(new IOException("private information")) : Task.FromResult(Snapshot("known")),
            () => throw new IOException("private fallback"), clock: () => Now);
        await service.RefreshAsync();
        failed = true;
        await service.RefreshAsync();
        Assert.Equal(UsageDataSource.LastConfirmed, service.Current.Source);
        Assert.Equal(Now, service.Current.UpdatedAt);
        Assert.NotNull(service.Current.LastAttemptAt);
        Assert.Equal(80, service.Current.Secondary!.RemainingPercent);
        Assert.Single(service.WeeklyHistory);
        Assert.DoesNotContain("private", service.Current.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewerUnverifiedLogCannotReplaceAConfirmedAccount()
    {
        var failed = false;
        using var service = _environment.CreateService(
            _ => failed ? Task.FromException<CodexUsageSnapshot>(new IOException()) : Task.FromResult(Snapshot("known")),
            () => Snapshot(null, 99) with { Source = UsageDataSource.LocalSession, UpdatedAt = Now.AddMinutes(1) }, clock: () => Now);
        await service.RefreshAsync();
        failed = true;
        await service.RefreshAsync();
        Assert.Equal(UsageDataSource.LastConfirmed, service.Current.Source);
        Assert.Equal("known", service.Current.AccountKey);
        Assert.Equal(80, service.Current.Secondary!.RemainingPercent);
    }

    [Fact]
    public async Task PresentationCapturesAnAccountAndItsHistoryTogether()
    {
        var next = Snapshot("a", 10);
        using var service = _environment.CreateService(_ => Task.FromResult(next), () => next, clock: () => Now);
        await service.RefreshAsync();
        var first = service.GetPresentation();
        next = Snapshot("b", 90);
        await service.RefreshAsync();
        var second = service.GetPresentation();
        Assert.Equal("a", first.Usage.AccountKey);
        Assert.Equal(90, Assert.Single(first.WeeklyHistory).RemainingPercent);
        Assert.Equal("b", second.Usage.AccountKey);
        Assert.Equal(10, Assert.Single(second.WeeklyHistory).RemainingPercent);
    }

    [Fact]
    public void ContextPathsDoNotContainExternalIdentifiersOrEscapeStorage()
    {
        var path = _environment.PathFor("weekly.json");
        var scoped = LocalStorage.ContextPath(path, "../../private@example.com");
        Assert.StartsWith(Path.GetDirectoryName(path) + Path.DirectorySeparatorChar, scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("private", scoped, StringComparison.Ordinal);
        Assert.Equal("weekly.json", Path.GetFileName(scoped));
    }
}
