using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class OptionalActionServiceTests : IDisposable
{
    private const string AccountA = "synthetic-account-a";
    private const string AccountB = "synthetic-account-b";
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    private string JournalPath => _environment.PathFor("optional-reset.json");

    [Fact]
    public async Task AmbiguousResetSurvivesRestartAndRetriesTheSameKeyWithoutAnotherReportedCredit()
    {
        string? firstKey = null;
        using (var first = CreateService(() => Quota(AccountA, 1), reset: (_, _, key, _) =>
        {
            firstKey = key;
            return Task.FromResult(new ResetCreditResult(ResetCreditOutcome.Ambiguous, Now));
        }))
        {
            await first.RefreshAsync().WaitAsync(Timeout);
            await first.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);
            Assert.NotNull(firstKey);
            Assert.Equal(firstKey, ReadPendingKey(AccountA));
        }

        string? retriedKey = null;
        using var restarted = CreateService(() => Quota(AccountA, 0), reset: (_, _, key, _) =>
        {
            retriedKey = key;
            return Task.FromResult(new ResetCreditResult(ResetCreditOutcome.AlreadyRedeemed, Now));
        });
        await restarted.RefreshAsync().WaitAsync(Timeout);
        await restarted.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);

        Assert.Equal(firstKey, retriedKey);
        Assert.Null(ReadPendingKey(AccountA));
        Assert.Contains("No second reset", restarted.ResetActionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRetryPreflightRetainsAnEarlierAmbiguousKey()
    {
        var keys = new List<string>();
        var call = 0;
        var quota = Quota(AccountA, 1);
        using var service = CreateService(() => quota, reset: (_, _, key, _) =>
        {
            keys.Add(key);
            var outcome = ++call switch
            {
                1 => ResetCreditOutcome.Ambiguous,
                2 => ResetCreditOutcome.Unavailable,
                _ => ResetCreditOutcome.AlreadyRedeemed,
            };
            return Task.FromResult(new ResetCreditResult(outcome, Now));
        });
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);
        var original = ReadPendingKey(AccountA);

        quota = Quota(AccountA, null);
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);
        Assert.Equal(original, ReadPendingKey(AccountA));
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);

        Assert.Equal(3, keys.Count);
        Assert.All(keys, key => Assert.Equal(original, key));
        Assert.Null(ReadPendingKey(AccountA));
    }

    [Fact]
    public async Task ConcurrentResetRequestsShareOneCallAndOneSavedKey()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<ResetCreditResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var service = CreateService(() => Quota(AccountA, 2), reset: (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            return result.Task;
        });
        await service.RefreshAsync().WaitAsync(Timeout);
        var first = service.ConsumeEarnedResetAsync(AccountA);
        await started.Task.WaitAsync(Timeout);
        var second = service.ConsumeEarnedResetAsync(AccountA);
        var third = service.ConsumeEarnedResetAsync(AccountA);
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.NotNull(ReadPendingKey(AccountA));

        result.SetResult(new(ResetCreditOutcome.Reset, Now));
        await Task.WhenAll(first, second, third).WaitAsync(Timeout);
        Assert.Equal(1, calls);
        Assert.Null(ReadPendingKey(AccountA));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task UnknownOrZeroCreditsDoNotCreateANewResetAttempt(int? available)
    {
        var calls = 0;
        using var service = CreateService(() => Quota(AccountA, available), reset: (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new ResetCreditResult(ResetCreditOutcome.Reset, Now));
        });
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);

        Assert.Equal(0, calls);
        Assert.Null(ReadPendingKey(AccountA));
        Assert.False(File.Exists(LocalStorage.ContextPath(JournalPath, AccountA)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task PendingResetCanBeRetriedWhenNoCreditCountIsAvailable(int? available)
    {
        var original = Guid.Parse("12345678-abcd-4321-abcd-1234567890ab").ToString("N");
        Assert.True(new ResetAttemptJournal(JournalPath).Save(AccountA, original));
        string? sentKey = null;
        using var service = CreateService(() => Quota(AccountA, available), reset: (_, _, key, _) =>
        {
            sentKey = key;
            return Task.FromResult(new ResetCreditResult(ResetCreditOutcome.AlreadyRedeemed, Now));
        });
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);

        Assert.Equal(original, sentKey);
        Assert.Null(ReadPendingKey(AccountA));
    }

    [Fact]
    public async Task ExistingOpaqueKeyKeepsItsExactCasingDuringRetry()
    {
        var original = Guid.Parse("abcdef12-abcd-4321-abcd-1234567890ab").ToString("N").ToUpperInvariant();
        Assert.True(new ResetAttemptJournal(JournalPath).Save(AccountA, original));
        string? sentKey = null;
        using var service = CreateService(() => Quota(AccountA, 0), reset: (_, _, key, _) =>
        {
            sentKey = key;
            return Task.FromResult(new ResetCreditResult(ResetCreditOutcome.Ambiguous, Now));
        });
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);

        Assert.Equal(original, sentKey);
        Assert.Equal(original, ReadPendingKey(AccountA));
    }

    [Fact]
    public async Task PendingKeyIsDurablySavedBeforeTheConsumerIsCalled()
    {
        string? sentKey = null;
        string? savedAtCall = null;
        var recordReadableAtCall = false;
        using var service = CreateService(() => Quota(AccountA, 1), reset: (_, account, key, _) =>
        {
            sentKey = key;
            recordReadableAtCall = new ResetAttemptJournal(JournalPath).TryRead(account, out savedAtCall);
            return Task.FromResult(new ResetCreditResult(ResetCreditOutcome.Ambiguous, Now));
        });
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);

        Assert.True(recordReadableAtCall);
        Assert.NotNull(sentKey);
        Assert.Equal(sentKey, savedAtCall);
        Assert.Equal(sentKey, ReadPendingKey(AccountA));
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":\"1\",\"pendingKey\":null}")]
    [InlineData("{\"schemaVersion\":2,\"pendingKey\":null}")]
    [InlineData("{\"schemaVersion\":1,\"pendingKey\":\"invalid-key\"}")]
    public async Task InvalidJournalBlocksTheActionWithoutOverwritingRecoveryData(string contents)
    {
        var path = LocalStorage.ContextPath(JournalPath, AccountA);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        var calls = 0;
        using var service = CreateService(() => Quota(AccountA, 1), reset: (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new ResetCreditResult(ResetCreditOutcome.Reset, Now));
        });
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);

        Assert.Equal(0, calls);
        Assert.Equal(contents, File.ReadAllText(path));
        Assert.Contains("No reset request was sent", service.ResetActionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JournalWriteFailurePreventsTheConsumerFromRunning()
    {
        Directory.CreateDirectory(LocalStorage.ContextPath(JournalPath, AccountA));
        var calls = 0;
        using var service = CreateService(() => Quota(AccountA, 1), reset: (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new ResetCreditResult(ResetCreditOutcome.Reset, Now));
        });
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);

        Assert.Equal(0, calls);
        Assert.Contains("could not be saved", service.ResetActionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LateResetResultDoesNotReplaceTheNewAccountStatus()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ResetCreditResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quota = Quota(AccountA, 1);
        using var service = CreateService(() => quota, reset: (_, _, _, _) =>
        {
            started.TrySetResult();
            return pending.Task;
        });
        await service.RefreshAsync().WaitAsync(Timeout);
        var action = service.ConsumeEarnedResetAsync(AccountA);
        await started.Task.WaitAsync(Timeout);
        quota = Quota(AccountB, 1);
        await service.RefreshAsync().WaitAsync(Timeout);
        var newAccountStatus = service.ResetActionStatus;
        pending.SetResult(new(ResetCreditOutcome.Reset, Now));
        await action.WaitAsync(Timeout);

        Assert.Equal(AccountB, service.Current.AccountKey);
        Assert.Equal(newAccountStatus, service.ResetActionStatus);
        Assert.Null(ReadPendingKey(AccountA));
        Assert.Null(ReadPendingKey(AccountB));
    }

    [Fact]
    public async Task LateTaskResultIsDiscardedAfterAnAccountChange()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ThreadUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quota = Quota(AccountA, 1);
        using var service = CreateService(() => quota, thread: (_, _, _, _) =>
        {
            started.TrySetResult();
            return pending.Task;
        });
        await service.RefreshAsync().WaitAsync(Timeout);
        var action = service.ReadTaskUsageAsync("task-a");
        await started.Task.WaitAsync(Timeout);
        quota = Quota(AccountB, 1);
        await service.RefreshAsync().WaitAsync(Timeout);
        var newAccountStatus = service.ThreadActionStatus;
        pending.SetResult(TaskResult("task-a", AccountA));
        await action.WaitAsync(Timeout);

        Assert.Null(service.CurrentThreadUsage);
        Assert.Equal(newAccountStatus, service.ThreadActionStatus);
    }

    [Fact]
    public async Task LatestRequestedTaskIsQueuedAndAnOlderTaskIsNeverPublishedAsItsResult()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<ThreadUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResult = new TaskCompletionSource<ThreadUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new ConcurrentQueue<string>();
        var calls = new ConcurrentQueue<string>();
        using var service = CreateService(() => Quota(AccountA, 1), thread: (_, id, _, _) =>
        {
            calls.Enqueue(id);
            if (id == "task-a")
            {
                firstStarted.TrySetResult();
                return firstResult.Task;
            }
            secondStarted.TrySetResult();
            return secondResult.Task;
        });
        service.Updated += (_, _) =>
        {
            if (service.CurrentThreadUsage?.ThreadId is { } id) published.Enqueue(id);
        };
        await service.RefreshAsync().WaitAsync(Timeout);
        var first = service.ReadTaskUsageAsync("task-a");
        await firstStarted.Task.WaitAsync(Timeout);
        var second = service.ReadTaskUsageAsync("task-b");
        firstResult.SetResult(TaskResult("task-a", AccountA));
        await secondStarted.Task.WaitAsync(Timeout);
        Assert.Null(service.CurrentThreadUsage);
        Assert.DoesNotContain("task-a", published);
        secondResult.SetResult(TaskResult("task-b", AccountA));
        await Task.WhenAll(first, second).WaitAsync(Timeout);

        Assert.Collection(calls, id => Assert.Equal("task-a", id), id => Assert.Equal("task-b", id));
        Assert.Equal("task-b", service.CurrentThreadUsage!.ThreadId);
        Assert.DoesNotContain("task-a", published);
    }

    [Theory]
    [InlineData(null, "task-a")]
    [InlineData(AccountB, "task-a")]
    [InlineData(AccountA, "foreign-task")]
    public async Task TaskResponsesWithoutMatchingAccountAndTaskAreNotUsed(string? responseAccount, string responseTask)
    {
        using var service = CreateService(() => Quota(AccountA, 1), thread: (_, _, _, _) =>
            Task.FromResult(TaskResult(responseTask, responseAccount)));
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ReadTaskUsageAsync("task-a").WaitAsync(Timeout);

        Assert.Null(service.CurrentThreadUsage);
        Assert.DoesNotContain("estimate reported", service.ThreadActionStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaleOrUnidentifiedUsageDoesNotAuthorizeResetOrTaskRequests()
    {
        var calls = 0;
        var quota = Quota(null, 1);
        using var service = CreateService(() => quota,
            reset: (_, _, _, _) => { calls++; return Task.FromResult(new ResetCreditResult(ResetCreditOutcome.Reset, Now)); },
            thread: (_, id, account, _) => { calls++; return Task.FromResult(TaskResult(id, account)); });
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);
        await service.ReadTaskUsageAsync("task-a").WaitAsync(Timeout);
        quota = Quota(AccountA, 1) with { UpdatedAt = Now.AddHours(-1) };
        await service.RefreshAsync().WaitAsync(Timeout);
        await service.ConsumeEarnedResetAsync(AccountA).WaitAsync(Timeout);
        await service.ReadTaskUsageAsync("task-a").WaitAsync(Timeout);

        Assert.Equal(0, calls);
        Assert.Null(service.CurrentThreadUsage);
        Assert.Null(ReadPendingKey(AccountA));
    }

    private CodexUsageService CreateService(Func<CodexUsageSnapshot> quota,
        Func<CodexSourceOptions, string, string, CancellationToken, Task<ResetCreditResult>>? reset = null,
        Func<CodexSourceOptions, string, string, CancellationToken, Task<ThreadUsageSnapshot>>? thread = null)
    {
        var service = _environment.CreateService(_ => Task.FromResult(quota()), () => quota(), clock: () => Now);
        service.InitializeOptionalFeatures(_environment.PathFor("optional-aggregates.json"), JournalPath, reset, thread);
        return service;
    }

    private string? ReadPendingKey(string account)
    {
        Assert.True(new ResetAttemptJournal(JournalPath).TryRead(account, out var key));
        return key;
    }

    private static CodexUsageSnapshot Quota(string? account, int? resets) => new(
        null, new RateLimitWindow(20, 10080, Now.AddDays(3)), null, null,
        resets is { } count ? new RateLimitResetCredits(count, null) : null,
        Now, UsageDataSource.AppServer, null, AccountKey: account, DefaultBucketId: "codex");

    private static ThreadUsageSnapshot TaskResult(string id, string? account) =>
        new(id, account, Now, ThreadUsageStatus.Available, EstimatedUsageCreditsMicros: 100, Groups: []);
}
