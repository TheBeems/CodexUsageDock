using System.Diagnostics;

namespace CodexUsageDock;

internal sealed partial class CodexUsageService : IDisposable
{
    internal const string LiveDataUnavailableMessage = "The live Codex service did not return usable data.";
    internal const string AllDataUnavailableMessage = "No usable data was returned by the live Codex service or local session history.";

    private readonly System.Timers.Timer _timer = new(TimeSpan.FromMinutes(1).TotalMilliseconds) { AutoReset = true };
    private readonly object _historyLock = new();
    private readonly object _refreshStateLock = new();
    private readonly List<UsageHistoryEntry> _primaryHistory = [];
    private readonly List<UsageHistoryEntry> _weeklyHistory = [];
    private readonly WeeklyUsageHistoryStore _baseWeeklyHistoryStore;
    private readonly AdaptiveWeeklyUsageStore _baseAdaptiveWeeklyUsageStore;
    private WeeklyUsageHistoryStore _weeklyHistoryStore;
    private AdaptiveWeeklyUsageStore _adaptiveWeeklyUsageStore;
    private string? _historyContext;
    private string? _memoryHistoryContext;
    private CodexUsageSnapshot? _lastConfirmed;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<CancellationToken, Task<CodexUsageSnapshot>> _appServerReader;
    private readonly Func<CancellationToken, CodexUsageSnapshot> _localSessionReader;
    private Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>>? _localTokenUsageReader;
    private readonly bool _usesConfiguredSources;
    private CodexSourceOptions _sourceOptions = CodexSourceOptions.Default;
    private long _sourceGeneration;
    private string? _sourceConfigurationError;
    private Task? _refreshTask;
    private Task<LocalTokenUsageSnapshot>? _tokenReadTask;
    private Task _tokenRefreshTask = Task.CompletedTask;
    private long _tokenGeneration;
    private bool _disposed;
    private bool _isLoading;
    private bool _started;
    private bool _adaptiveWeeklyForecastEnabled = true;
    private bool _adaptiveWeeklyForecastNeedsBaseline;

    public CodexUsageService()
        : this(
            CodexAppServerReader.ReadAsync,
            LocalCodexSessionReader.ReadLatest,
            WeeklyUsageHistoryStore.CreateDefault(),
            AdaptiveWeeklyUsageStore.CreateDefault(),
            localTokenUsageReader: new LocalCodexTokenUsageReader().ReadAsync)
    {
        _usesConfiguredSources = true;
    }

    internal CodexUsageService(
        Func<CancellationToken, Task<CodexUsageSnapshot>> appServerReader,
        Func<CancellationToken, CodexUsageSnapshot> localSessionReader,
        WeeklyUsageHistoryStore weeklyHistoryStore,
        AdaptiveWeeklyUsageStore adaptiveWeeklyUsageStore,
        Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>>? localTokenUsageReader = null,
        Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<AccountUsageSnapshot>>? accountUsageReader = null)
    {
        _appServerReader = appServerReader;
        _localSessionReader = localSessionReader;
        _localTokenUsageReader = localTokenUsageReader;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _accountUsageReader = accountUsageReader;
        _weeklyHistoryStore = _baseWeeklyHistoryStore = weeklyHistoryStore;
        _adaptiveWeeklyUsageStore = _baseAdaptiveWeeklyUsageStore = adaptiveWeeklyUsageStore;
        // Legacy files have no account identity. They must never seed a verified account.
    }

    internal CodexUsageService(
        Func<CancellationToken, Task<CodexUsageSnapshot>> appServerReader,
        Func<CodexUsageSnapshot> localSessionReader,
        WeeklyUsageHistoryStore weeklyHistoryStore,
        AdaptiveWeeklyUsageStore adaptiveWeeklyUsageStore,
        Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>>? localTokenUsageReader = null,
        Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<AccountUsageSnapshot>>? accountUsageReader = null)
        : this(appServerReader, _ => localSessionReader(), weeklyHistoryStore, adaptiveWeeklyUsageStore, localTokenUsageReader, clock, accountUsageReader)
    {
    }

    public CodexUsageSnapshot Current { get; private set; } = CodexUsageSnapshot.Loading;

    public LocalTokenUsageSnapshot CurrentTokenUsage { get; private set; } = LocalTokenUsageSnapshot.Unavailable;

    internal UsagePresentation GetPresentation()
    {
        lock (_refreshStateLock)
        {
            lock (_historyLock)
            {
                return new(Current, _primaryHistory.ToArray(), _weeklyHistory.ToArray(),
                    _historyContext is null ? new([], null) : _adaptiveWeeklyUsageStore.Snapshot,
                    CurrentTokenUsage, _isLoading);
            }
        }
    }

    internal Task TokenRefreshTask
    {
        get
        {
            lock (_refreshStateLock)
            {
                return _tokenRefreshTask;
            }
        }
    }

    public bool IsLoading
    {
        get
        {
            lock (_refreshStateLock)
            {
                return _isLoading;
            }
        }
    }

    public IReadOnlyList<UsageHistoryEntry> PrimaryHistory
    {
        get
        {
            lock (_historyLock)
            {
                return _primaryHistory.ToArray();
            }
        }
    }

    public IReadOnlyList<UsageHistoryEntry> WeeklyHistory
    {
        get
        {
            lock (_historyLock)
            {
                return _weeklyHistory.ToArray();
            }
        }
    }

    public TimeSpan RefreshInterval => TimeSpan.FromMilliseconds(_timer.Interval);

    public AdaptiveWeeklyUsageHistory AdaptiveWeeklyHistory
    {
        get
        {
            lock (_historyLock)
            {
                return _historyContext is null ? new([], null) : _adaptiveWeeklyUsageStore.Snapshot;
            }
        }
    }

    public event EventHandler? Updated;

    internal bool ConfigureSource(CodexSourceOptions options, string? error = null)
    {
        lock (_refreshStateLock)
        {
            if (_sourceOptions == options && _sourceConfigurationError == error) return false;
            _sourceOptions = options;
            _sourceConfigurationError = error;
            _sourceGeneration++;
            _tokenGeneration++;
            _lastConfirmed = null;
            Current = CodexUsageSnapshot.Loading;
            CurrentTokenUsage = LocalTokenUsageSnapshot.Unavailable;
            CurrentAccountUsage = AccountUsageSnapshot.Unavailable;
            _accountReadAfter = DateTimeOffset.MinValue;
            if (_usesConfiguredSources)
            {
                _localTokenUsageReader = new LocalCodexTokenUsageReader(options.HomePath).ReadAsync;
            }
            lock (_historyLock)
            {
                _memoryHistoryContext = null;
                _historyContext = null;
                _primaryHistory.Clear();
                _weeklyHistory.Clear();
            }
        }
        RaiseUpdated();
        return true;
    }

    public void Start()
    {
        lock (_refreshStateLock)
        {
            if (_disposed || _started)
            {
                return;
            }

            _started = true;
            _timer.Elapsed += OnTimer;
            _timer.Start();
        }

        _ = RefreshAsync();
    }

    internal void SetRefreshInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The refresh interval must be at least one minute.");
        }

        _timer.Interval = interval.TotalMilliseconds;
    }

    internal void SetAdaptiveWeeklyForecastEnabled(bool enabled)
    {
        lock (_historyLock)
        {
            if (enabled == _adaptiveWeeklyForecastEnabled)
            {
                return;
            }

            _adaptiveWeeklyForecastEnabled = enabled;
            if (enabled)
            {
                _adaptiveWeeklyForecastNeedsBaseline = true;
            }
            else
            {
                _adaptiveWeeklyForecastNeedsBaseline = false;
            }
        }
    }

    internal bool ClearAdaptiveWeeklyHistory()
    {
        lock (_historyLock)
        {
            return _historyContext is null || _adaptiveWeeklyUsageStore.Clear();
        }
    }

    internal string? HistoryStorageError
    {
        get
        {
            lock (_historyLock)
            {
                return _weeklyHistoryStore.StorageError ?? _adaptiveWeeklyUsageStore.StorageError;
            }
        }
    }

    public Task RefreshAsync()
    {
        TaskCompletionSource completion;
        CancellationToken cancellationToken;
        CodexSourceOptions options;
        long generation;
        lock (_refreshStateLock)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (_refreshTask is { IsCompleted: false })
            {
                return _refreshTask;
            }

            _isLoading = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _refreshTask = completion.Task;
            cancellationToken = _lifetimeCancellation.Token;
            options = _sourceOptions;
            generation = _sourceGeneration;
        }

        RaiseUpdated();
        _ = ExecuteRefreshAsync(completion, options, generation, cancellationToken);
        return completion.Task;
    }

    internal void RecordHistory(CodexUsageSnapshot snapshot, DateTimeOffset? recordedAt = null)
    {
        lock (_historyLock)
        {
            var now = recordedAt ?? _clock();
            if (snapshot.Source is UsageDataSource.LastConfirmed or UsageDataSource.Unavailable or UsageDataSource.Initializing)
            {
                _primaryHistory.RemoveAll(entry => entry.RecordedAt < now.AddHours(-5));
                _weeklyHistory.RemoveAll(entry => entry.RecordedAt < now.AddDays(-7));
                return;
            }

            var context = snapshot.AccountKey is { Length: > 0 } account
                ? $"{account}|{snapshot.DefaultBucketId ?? "codex"}"
                : null;
            var memoryContext = context ?? $"unverified|{snapshot.DefaultBucketId ?? "codex"}";
            if (!string.Equals(memoryContext, _memoryHistoryContext, StringComparison.Ordinal))
            {
                _memoryHistoryContext = memoryContext;
                _historyContext = context;
                _primaryHistory.Clear();
                _weeklyHistory.Clear();
                _weeklyHistoryStore = context is null ? _baseWeeklyHistoryStore : _baseWeeklyHistoryStore.ForContext(context);
                _adaptiveWeeklyUsageStore = context is null ? _baseAdaptiveWeeklyUsageStore : _baseAdaptiveWeeklyUsageStore.ForContext(context);
                if (context is not null)
                {
                    _weeklyHistory.AddRange(_weeklyHistoryStore.Load(now));
                }
                // A returning account may have been observed while learning was paused.
                // Resume at this measurement rather than replaying another context's gap.
                _adaptiveWeeklyForecastNeedsBaseline = _adaptiveWeeklyForecastEnabled;
            }

            RecordWindowHistory(_primaryHistory, snapshot.Primary, snapshot.UpdatedAt, now, now - TimeSpan.FromHours(5));
            var weeklyHistoryChanged = RecordWindowHistory(_weeklyHistory, snapshot.Secondary, snapshot.UpdatedAt, now, now - TimeSpan.FromDays(7));
            if (weeklyHistoryChanged && context is not null)
            {
                _weeklyHistoryStore.Save(_weeklyHistory);
            }

            if (context is not null && _adaptiveWeeklyForecastEnabled && (weeklyHistoryChanged || _adaptiveWeeklyForecastNeedsBaseline))
            {
                if (_adaptiveWeeklyForecastNeedsBaseline)
                {
                    _adaptiveWeeklyUsageStore.AdvanceBaseline(snapshot.Secondary, _weeklyHistory);
                    _adaptiveWeeklyForecastNeedsBaseline = false;
                }
                else
                {
                    _adaptiveWeeklyUsageStore.Record(
                        snapshot.Secondary,
                        _weeklyHistory,
                        GetAdaptiveMaximumGap());
                }
            }
        }
    }

    private static bool RecordWindowHistory(
        List<UsageHistoryEntry> history,
        RateLimitWindow? window,
        DateTimeOffset updatedAt,
        DateTimeOffset now,
        DateTimeOffset cutoff)
    {
        var changed = history.RemoveAll(entry => entry.RecordedAt < cutoff) > 0;
        if (window is null || updatedAt < cutoff || updatedAt > now || !double.IsFinite(window.RemainingPercent))
        {
            return changed;
        }

        if (history.Count > 0 && updatedAt <= history[^1].RecordedAt)
        {
            return changed;
        }

        history.Add(new UsageHistoryEntry(
            updatedAt,
            window.RemainingPercent,
            window.ResetsAt,
            window.WindowMinutes));
        return true;
    }

    private TimeSpan GetAdaptiveMaximumGap() =>
        UsageTrendHistory.MaximumGap(RefreshInterval);

    private async Task ExecuteRefreshAsync(TaskCompletionSource completion, CodexSourceOptions options, long generation, CancellationToken cancellationToken)
    {
        CodexUsageSnapshot? tokenSnapshot = null;
        try
        {
            var snapshot = await ReadSnapshotAsync(options, generation, cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && TryPublish(snapshot, generation))
            {
                tokenSnapshot = snapshot;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            TraceFailure("unexpected refresh", error);
            var unavailable = FailureSnapshot(_clock());
            _ = TryPublish(unavailable, generation);
        }
        finally
        {
            lock (_refreshStateLock)
            {
                _isLoading = false;
            }

            RaiseUpdated();
            if (tokenSnapshot is not null)
            {
                StartTokenRefresh(tokenSnapshot);
                StartAccountUsageRefresh(tokenSnapshot);
            }
            completion.TrySetResult();
            if (generation != _sourceGeneration && !cancellationToken.IsCancellationRequested)
            {
                _ = RefreshAsync();
            }
        }
    }

    private async Task<CodexUsageSnapshot> ReadSnapshotAsync(CodexSourceOptions options, long generation, CancellationToken cancellationToken)
    {
        var attemptedAt = _clock();
        lock (_refreshStateLock)
        {
            if (_sourceConfigurationError is not null)
                return CreateUnavailableSnapshot() with { LastAttemptAt = attemptedAt };
        }
        try
        {
            var live = await (_usesConfiguredSources
                ? CodexAppServerReader.ReadAsync(options, cancellationToken)
                : _appServerReader(cancellationToken)).ConfigureAwait(false);
            lock (_refreshStateLock)
            {
                if (live.Source == UsageDataSource.AppServer && generation == _sourceGeneration)
                {
                    _lastConfirmed = live;
                }
            }
            return live with { LastAttemptAt = attemptedAt };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            TraceFailure("live usage read", error);
            try
            {
                var fallback = await Task.Run(() => _usesConfiguredSources
                    ? LocalCodexSessionReader.ReadLatest(options.HomePath ?? LocalStorage.GetCodexHome(), _clock(), cancellationToken)
                    : _localSessionReader(cancellationToken), cancellationToken).ConfigureAwait(false);
                // Session logs do not normally identify the signed-in account. A newer
                // unverified log must not replace a confirmed, account-scoped measurement.
                if (_lastConfirmed is { } confirmed
                    && (fallback.UpdatedAt <= confirmed.UpdatedAt
                        || (confirmed.AccountKey is not null && fallback.AccountKey != confirmed.AccountKey)))
                {
                    return LastConfirmedSnapshot(confirmed, attemptedAt);
                }
                return fallback with { Error = LiveDataUnavailableMessage, LastAttemptAt = attemptedAt };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception fallbackError)
            {
                TraceFailure("local session fallback", fallbackError);
                return FailureSnapshot(attemptedAt);
            }
        }
    }

    private CodexUsageSnapshot FailureSnapshot(DateTimeOffset attemptedAt)
    {
        lock (_refreshStateLock)
        {
            return _lastConfirmed is { } confirmed
                ? LastConfirmedSnapshot(confirmed, attemptedAt)
                : CreateUnavailableSnapshot() with { LastAttemptAt = attemptedAt };
        }
    }

    private static CodexUsageSnapshot LastConfirmedSnapshot(CodexUsageSnapshot confirmed, DateTimeOffset attemptedAt) => confirmed with
    {
        Source = UsageDataSource.LastConfirmed,
        LastAttemptAt = attemptedAt,
        Error = LiveDataUnavailableMessage,
    };

    private async Task<LocalTokenUsageSnapshot> ReadTokenUsageAsync(
        CodexUsageSnapshot snapshot,
        Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>> reader,
        CancellationToken cancellationToken)
    {
        if (snapshot.Secondary is not { WindowMinutes: > 0 } weekly)
        {
            return LocalTokenUsageSnapshot.Unavailable with { UpdatedAt = _clock() };
        }

        var windowStart = weekly.ResetsAt - TimeSpan.FromMinutes(weekly.WindowMinutes);
        var now = _clock();
        var windowEnd = now < weekly.ResetsAt ? now : weekly.ResetsAt;
        try
        {
            return await reader(windowStart, windowEnd, TimeZoneInfo.Local, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            TraceFailure("local token usage read", error);
            return LocalTokenUsageSnapshot.Unavailable with { UpdatedAt = _clock() };
        }
    }

    private void StartTokenRefresh(CodexUsageSnapshot snapshot)
    {
        lock (_refreshStateLock)
        {
            if (_disposed || !ReferenceEquals(Current, snapshot) || _localTokenUsageReader is null || snapshot.Secondary is null
                || snapshot.Source is UsageDataSource.LastConfirmed or UsageDataSource.Unavailable or UsageDataSource.Initializing
                || _tokenReadTask is { IsCompleted: false })
            {
                return;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(20));
            // Keep the actual read task, even after a timeout, so a non-cooperative
            // file read cannot cause overlapping scans of the reader's mutable cache.
            var generation = _tokenGeneration;
            var reader = _localTokenUsageReader;
            var read = _tokenReadTask = Task.Run(() => ReadTokenUsageAsync(snapshot, reader, cancellation.Token));
            // Notifications must run outside the state lock, even if the read finishes immediately.
            _tokenRefreshTask = Task.Run(() => ObserveTokenRefreshAsync(read, cancellation, generation));
        }
    }

    private async Task ObserveTokenRefreshAsync(Task<LocalTokenUsageSnapshot> read, CancellationTokenSource cancellation, long generation)
    {
        try
        {
            var result = await read.WaitAsync(cancellation.Token).ConfigureAwait(false);
            lock (_refreshStateLock)
            {
                if (_disposed || generation != _tokenGeneration)
                {
                    return;
                }

                CurrentTokenUsage = result;
            }

            RaiseUpdated();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            TraceFailure("token refresh", error);
        }
        finally
        {
            if (read.IsCompleted)
            {
                cancellation.Dispose();
            }
            else
            {
                _ = read.ContinueWith(task =>
                {
                    _ = task.Exception;
                    cancellation.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private bool TryPublish(CodexUsageSnapshot snapshot, long generation)
    {
        lock (_refreshStateLock)
        {
            if (_disposed || generation != _sourceGeneration)
            {
                return false;
            }

            RecordHistory(snapshot);
            if (Current.Secondary?.ResetsAt != snapshot.Secondary?.ResetsAt
                || Current.AccountKey != snapshot.AccountKey || Current.DefaultBucketId != snapshot.DefaultBucketId
                || snapshot.Source == UsageDataSource.Unavailable)
            {
                CurrentTokenUsage = LocalTokenUsageSnapshot.Unavailable;
                if (Current.AccountKey != snapshot.AccountKey)
                {
                    CurrentAccountUsage = AccountUsageSnapshot.Unavailable;
                    _accountReadAfter = DateTimeOffset.MinValue;
                }
            }

            Current = snapshot;
            _tokenGeneration++;
            return true;
        }
    }

    private CodexUsageSnapshot CreateUnavailableSnapshot() => CodexUsageSnapshot.Loading with
    {
        UpdatedAt = _clock(),
        Source = UsageDataSource.Unavailable,
        Error = AllDataUnavailableMessage,
    };

    private void RaiseUpdated()
    {
        lock (_refreshStateLock)
        {
            if (_disposed)
            {
                return;
            }
        }

        var handlers = Updated;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception error)
            {
                TraceFailure("update subscriber", error);
            }
        }
    }

    private static void TraceFailure(string operation, Exception error)
    {
        var rootCause = error.GetBaseException();
        Trace.TraceWarning(
            "Codex Usage Dock: {0} failed ({1}, HRESULT 0x{2:X8}); sensitive exception messages are omitted.",
            operation,
            rootCause.GetType().Name,
            rootCause.HResult);
    }

    private void OnTimer(object? sender, System.Timers.ElapsedEventArgs e) => _ = RefreshAsync();

    public void Dispose()
    {
        Task? refreshTask;
        lock (_refreshStateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            refreshTask = Task.WhenAll(_refreshTask ?? Task.CompletedTask, _tokenRefreshTask, _accountRefreshTask);
        }

        _timer.Stop();
        _timer.Elapsed -= OnTimer;
        _timer.Dispose();
        _lifetimeCancellation.Cancel();

        if (refreshTask is { IsCompleted: false })
        {
            _ = refreshTask.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _lifetimeCancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            _lifetimeCancellation.Dispose();
        }
    }
}
