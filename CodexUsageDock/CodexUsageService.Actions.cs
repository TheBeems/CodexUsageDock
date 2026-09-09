namespace CodexUsageDock;

internal sealed partial class CodexUsageService
{
    private ResetAttemptJournal? _resetJournal;
    private Func<CodexSourceOptions, string, string, CancellationToken, Task<ResetCreditResult>>? _resetCreditConsumer;
    private Func<CodexSourceOptions, string, string, CancellationToken, Task<ThreadUsageSnapshot>>? _threadUsageReader;
    private Task<string>? _resetActionTask;
    private Task? _threadActionTask;
    private PendingTaskRead? _pendingTaskRead;
    private long _taskRequestVersion;
    private sealed record PendingTaskRead(CodexSourceOptions Options, string ThreadId, string Account,
        long SourceGeneration, long RequestVersion);
    internal string ResetActionStatus { get; private set; } = "Only an explicit, confirmed action can use an existing earned reset.";
    internal ThreadUsageSnapshot? CurrentThreadUsage { get; private set; }
    internal string ThreadActionStatus { get; private set; } = "Enter a Codex task ID to request its reported usage estimate.";

    internal (CodexUsageSnapshot Usage, ThreadUsageSnapshot? Task, string TaskStatus, string ResetStatus) GetActionPresentation()
    {
        lock (_refreshStateLock) { return (Current, CurrentThreadUsage, ThreadActionStatus, ResetActionStatus); }
    }

    internal void InitializeOptionalFeatures(string aggregatePath, string resetJournalPath,
        Func<CodexSourceOptions, string, string, CancellationToken, Task<ResetCreditResult>>? resetConsumer = null,
        Func<CodexSourceOptions, string, string, CancellationToken, Task<ThreadUsageSnapshot>>? threadReader = null)
    {
        if (_started) throw new InvalidOperationException("Optional storage must be configured before monitoring starts.");
        _aggregatePath = Path.GetFullPath(aggregatePath);
        _resetJournal = new ResetAttemptJournal(Path.GetFullPath(resetJournalPath));
        _resetCreditConsumer = resetConsumer ?? (_usesConfiguredSources ? CodexAppServerReader.ConsumeResetCreditAsync : null);
        _threadUsageReader = threadReader ?? (_usesConfiguredSources ? CodexAppServerReader.ReadThreadUsageAsync : null);
    }

    internal Task<string> ConsumeEarnedResetAsync(string expectedAccountKey)
    {
        Task<string> task;
        lock (_refreshStateLock)
        {
            if (_resetActionTask is { IsCompleted: false }) return _resetActionTask;
            if (_disposed || _resetJournal is null || _resetCreditConsumer is null
                || !IsFreshAccount(expectedAccountKey))
            {
                ResetActionStatus = "Refresh a live, identified Codex account before using a reset.";
                task = Task.FromResult(ResetActionStatus);
            }
            else
            {
                var options = _sourceOptions;
                var generation = _sourceGeneration;
                var available = Current.ResetCredits?.AvailableCount;
                ResetActionStatus = "Checking the existing reset attempt…";
                task = _resetActionTask = Task.Run(() => RunResetAsync(options, expectedAccountKey, available, generation));
            }
        }
        RaiseUpdated();
        return task;
    }

    private async Task<string> RunResetAsync(CodexSourceOptions options, string account, int? available, long generation)
    {
        string message;
        if (!_resetJournal!.TryRead(account, out var key))
            message = "The pending reset record could not be read safely. No reset request was sent.";
        else if (key is null && available is not > 0)
            message = "No available earned reset is currently reported. No reset request was sent.";
        else
        {
            key ??= Guid.NewGuid().ToString("N");
            if (!_resetJournal.Save(account, key))
                message = "The reset request ID could not be saved. No reset request was sent.";
            else
            {
                ResetCreditResult result;
                try
                {
                    result = await _resetCreditConsumer!(options, account, key, _lifetimeCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    LocalStorage.TraceFailure("request earned reset", error);
                    result = new(ResetCreditOutcome.Ambiguous, _clock());
                }
                message = DescribeResetOutcome(result.Outcome);
                // Any unresolved attempt, including a retry whose preflight failed, keeps its original key.
                if (result.Outcome is ResetCreditOutcome.Reset or ResetCreditOutcome.AlreadyRedeemed
                    or ResetCreditOutcome.NothingToReset or ResetCreditOutcome.NoCredit)
                {
                    if (!_resetJournal.Save(account, null))
                        message += " The recovery record could not be cleared; a retry will reuse the same request ID.";
                }
                await RefreshAsync().ConfigureAwait(false);
            }
        }
        lock (_refreshStateLock)
        {
            if (!_disposed && generation == _sourceGeneration && Current.AccountKey == account)
                ResetActionStatus = message;
        }
        RaiseUpdated();
        return message;
    }

    internal static string DescribeResetOutcome(ResetCreditOutcome outcome) => outcome switch
    {
        ResetCreditOutcome.Reset => "An earned reset was applied. Usage limits have been requested again.",
        ResetCreditOutcome.AlreadyRedeemed => "This request had already redeemed its reset. No second reset was used.",
        ResetCreditOutcome.NothingToReset => "The service reports nothing to reset. No reset was applied.",
        ResetCreditOutcome.NoCredit => "The service reports no eligible earned reset. No reset was applied.",
        ResetCreditOutcome.Unsupported => "This Codex CLI does not support earned resets. The request ID is retained for a safe retry.",
        ResetCreditOutcome.AccountMismatch => "The Codex account changed before the request. No reset was sent to the changed account.",
        ResetCreditOutcome.Unavailable => "The service could not be checked before sending the reset. The request ID is retained.",
        _ => "The reset outcome is unknown. Confirm a retry to reuse the same request ID, including after restarting the extension.",
    };

    private bool IsFreshAccount(string expectedAccountKey) => !string.IsNullOrWhiteSpace(expectedAccountKey)
        && Current.AccountKey == expectedAccountKey && Current.Source == UsageDataSource.AppServer
        && !_isLoading && UsageFreshness.IsFresh(Current.UpdatedAt, _clock(), RefreshInterval);

    internal Task ReadTaskUsageAsync(string threadId)
    {
        Task task;
        lock (_refreshStateLock)
        {
            if (_disposed || _threadUsageReader is null || Current.AccountKey is not { } account || !IsFreshAccount(account))
            {
                ThreadActionStatus = "Refresh a live, identified account before requesting task usage.";
                CurrentThreadUsage = null;
                task = Task.CompletedTask;
            }
            else if (!ThreadUsageParser.IsValidThreadId(threadId?.Trim()))
            {
                ThreadActionStatus = "Enter a task ID of at most 128 letters, digits, hyphens, or underscores.";
                CurrentThreadUsage = null;
                task = Task.CompletedTask;
            }
            else
            {
                _pendingTaskRead = new(_sourceOptions, threadId!.Trim(), account, _sourceGeneration, ++_taskRequestVersion);
                ThreadActionStatus = "Reading the latest requested task estimate…";
                CurrentThreadUsage = null;
                task = _threadActionTask is { IsCompleted: false } active ? active
                    : _threadActionTask = Task.Run(RunTaskReadsAsync);
            }
        }
        RaiseUpdated();
        return task;
    }

    private async Task RunTaskReadsAsync()
    {
        while (true)
        {
            PendingTaskRead request;
            lock (_refreshStateLock)
            {
                if (_disposed || _pendingTaskRead is null) { _threadActionTask = null; return; }
                request = _pendingTaskRead;
                _pendingTaskRead = null;
            }
            ThreadUsageSnapshot? result = null;
            try { result = await _threadUsageReader!(request.Options, request.ThreadId, request.Account, _lifetimeCancellation.Token).ConfigureAwait(false); }
            catch (Exception error) { LocalStorage.TraceFailure("read task usage", error); }
            lock (_refreshStateLock)
            {
                if (_disposed || request.SourceGeneration != _sourceGeneration || Current.AccountKey != request.Account
                    || request.RequestVersion != _taskRequestVersion) continue;
                var usable = result?.AccountKey == request.Account && result.ThreadId == request.ThreadId
                    && result.Status is ThreadUsageStatus.Available or ThreadUsageStatus.Partial;
                CurrentThreadUsage = usable ? result : null;
                ThreadActionStatus = result?.Status switch
                {
                    ThreadUsageStatus.Available when usable => "Task usage estimate reported by Codex.",
                    ThreadUsageStatus.Partial when usable => "Partial task estimate; missing values are not zero.",
                    ThreadUsageStatus.Unsupported => "This Codex CLI does not support task usage estimates.",
                    ThreadUsageStatus.AccountMismatch => "The account changed during the read. The result was discarded.",
                    _ => "Task usage is unavailable. Check the task ID and try again.",
                };
            }
            RaiseUpdated();
        }
    }

    private void ClearActionPresentation()
    {
        CurrentThreadUsage = null;
        _pendingTaskRead = null;
        _taskRequestVersion++;
        ThreadActionStatus = "Enter a Codex task ID to request its reported usage estimate.";
        ResetActionStatus = "Only an explicit, confirmed action can use an existing earned reset.";
    }
}
