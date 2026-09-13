using Xunit;

namespace CodexUsageDock.Tests;

public sealed class SourceAndActivityServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly TestEnvironment _environment = new();
    public void Dispose() => _environment.Dispose();

    private static CodexUsageSnapshot Quota(string account) => new(null, new(20, 10080, Now.AddDays(3)),
        null, null, null, Now, UsageDataSource.AppServer, null, AccountKey: account, DefaultBucketId: "codex");

    [Fact]
    public void SourcePathsAreExplicitAndErrorsDoNotExposeTheirValues()
    {
        var home = _environment.PathFor("profile");
        Directory.CreateDirectory(home);
        var executable = _environment.PathFor("codex.exe");
        File.WriteAllText(executable, "synthetic fixture; never executed");
        Assert.True(CodexSourceOptions.TryCreate(executable, home, out var options, out var error));
        Assert.Null(error);
        Assert.Equal(executable, options.ExecutablePath);
        Assert.Equal(home, options.HomePath);
        Assert.False(CodexSourceOptions.TryCreate("private-relative-path", null, out _, out error));
        Assert.DoesNotContain("private-relative-path", error!, StringComparison.Ordinal);
        Assert.False(CodexSourceOptions.TryCreate(executable + " --argument", home, out _, out _));
    }

    [Fact]
    public async Task InvalidSourceConfigurationFailsClosedWithoutReadingAnotherProfile()
    {
        var calls = 0;
        using var service = _environment.CreateService(_ => { calls++; return Task.FromResult(Quota("a")); },
            () => { calls++; return Quota("b"); }, clock: () => Now);
        service.ConfigureSource(CodexSourceOptions.Default, "Invalid source configuration.");
        await service.RefreshAsync();
        Assert.Equal(0, calls);
        Assert.Equal(UsageDataSource.Unavailable, service.Current.Source);
    }

    [Fact]
    public async Task SlowAccountActivityDoesNotBlockQuotasOrPublishForAnotherAccount()
    {
        var pending = new TaskCompletionSource<AccountUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = Quota("a");
        using var service = _environment.CreateService(_ => Task.FromResult(snapshot), () => snapshot,
            clock: () => Now, accountUsageReader: _ => pending.Task);
        await service.RefreshAsync();
        var activityTask = service.AccountUsageRefreshTask;
        Assert.Equal("a", service.Current.AccountKey);
        Assert.False(activityTask.IsCompleted);
        snapshot = Quota("b");
        await service.RefreshAsync();
        pending.SetResult(new("a", Now, [new(DateOnly.FromDateTime(Now.Date), 123)], AccountUsageStatus.Available));
        await activityTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AccountUsageStatus.Unavailable, service.CurrentAccountUsage.Status);
        Assert.Equal("b", service.Current.AccountKey);
    }

    [Fact]
    public async Task AccountUsageCanBeDisabledAndUnsupportedReadsAreThrottled()
    {
        var now = Now;
        var calls = 0;
        using var service = _environment.CreateService(_ => Task.FromResult(Quota("a")), () => Quota("a"),
            clock: () => now, accountUsageReader: _ =>
            {
                calls++;
                return Task.FromResult(AccountUsageSnapshot.Unavailable with { Status = AccountUsageStatus.Unsupported });
            });
        service.SetAccountActivityEnabled(false);
        await service.RefreshAsync();
        Assert.Equal(0, calls);
        service.SetAccountActivityEnabled(true);
        await service.RefreshAsync();
        await service.AccountUsageRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        now = Now.AddMinutes(6);
        await service.RefreshAsync();
        Assert.Equal(1, calls);
        now = Now.AddMinutes(31);
        await service.RefreshAsync();
        await service.AccountUsageRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DisablingActivityAndSwitchingSourcePublishClearedStateImmediately()
    {
        using var service = _environment.CreateService(_ => Task.FromResult(Quota("a")), () => Quota("a"),
            clock: () => Now, accountUsageReader: _ => Task.FromResult(new AccountUsageSnapshot("a", Now, [], AccountUsageStatus.Available)));
        await service.RefreshAsync();
        await service.AccountUsageRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AccountUsageStatus.Available, service.CurrentAccountUsage.Status);
        var updates = 0;
        service.Updated += (_, _) => updates++;
        service.SetAccountActivityEnabled(false);
        Assert.Equal(1, updates);
        Assert.Equal(AccountUsageStatus.Unavailable, service.CurrentAccountUsage.Status);
        service.ConfigureSource(new(HomePath: _environment.PathFor("new-profile")));
        Assert.Equal(2, updates);
        Assert.Equal(UsageDataSource.Initializing, service.Current.Source);
        Assert.Empty(service.WeeklyHistory);
    }

    [Fact]
    public async Task SourceSwitchDiscardsTheOldInFlightQuotaResponse()
    {
        var pending = new TaskCompletionSource<CodexUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var service = _environment.CreateService(_ => ++calls == 1 ? pending.Task : Task.FromResult(Quota("b")),
            () => Quota("fallback"), clock: () => Now);
        var first = service.RefreshAsync();
        service.ConfigureSource(new(HomePath: _environment.PathFor("profile")));
        pending.SetResult(Quota("a"));
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("b", service.Current.AccountKey);
        Assert.Equal(80, Assert.Single(service.WeeklyHistory).RemainingPercent);
    }
}
