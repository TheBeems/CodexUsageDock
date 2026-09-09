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
    private readonly WeeklyUsageHistoryStore _weeklyHistoryStore;
    private readonly AdaptiveWeeklyUsageStore _adaptiveWeeklyUsageStore;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Func<CancellationToken, Task<CodexUsageSnapshot>> _appServerReader;
    private readonly Func<CancellationToken, CodexUsageSnapshot> _localSessionReader;
    private readonly Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>>? _localTokenUsageReader;
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
    }

    internal CodexUsageService(
        Func<CancellationToken, Task<CodexUsageSnapshot>> appServerReader,
        Func<CancellationToken, CodexUsageSnapshot> localSessionReader,
        WeeklyUsageHistoryStore weeklyHistoryStore,
        AdaptiveWeeklyUsageStore adaptiveWeeklyUsageStore,
        Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>>? localTokenUsageReader = null)
    {
        _appServerReader = appServerReader;
        _localSessionReader = localSessionReader;
        _localTokenUsageReader = localTokenUsageReader;
        _weeklyHistoryStore = weeklyHistoryStore;
        _adaptiveWeeklyUsageStore = adaptiveWeeklyUsageStore;
        _weeklyHistory.AddRange(_weeklyHistoryStore.Load(DateTimeOffset.Now));
    }

    internal CodexUsageService(
        Func<CancellationToken, Task<CodexUsageSnapshot>> appServerReader,
        Func<CodexUsageSnapshot> localSessionReader,
        WeeklyUsageHistoryStore weeklyHistoryStore,
        AdaptiveWeeklyUsageStore adaptiveWeeklyUsageStore,
        Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>>? localTokenUsageReader = null)
        : this(appServerReader, _ => localSessionReader(), weeklyHistoryStore, adaptiveWeeklyUsageStore, localTokenUsageReader)
    {
    }

    public CodexUsageSnapshot Current { get; private set; } = CodexUsageSnapshot.Loading;

    public LocalTokenUsageSnapshot CurrentTokenUsage { get; private set; } = LocalTokenUsageSnapshot.Unavailable;

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
                return _adaptiveWeeklyUsageStore.Snapshot;
            }
        }
    }

    public event EventHandler? Updated;

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
            return _adaptiveWeeklyUsageStore.Clear();
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
        }

        RaiseUpdated();
        _ = ExecuteRefreshAsync(completion, cancellationToken);
        return completion.Task;
    }

    internal void RecordHistory(CodexUsageSnapshot snapshot, DateTimeOffset? recordedAt = null)
    {
        lock (_historyLock)
        {
            var now = recordedAt ?? DateTimeOffset.Now;
            RecordWindowHistory(_primaryHistory, snapshot.Primary, snapshot.UpdatedAt, now, now - TimeSpan.FromHours(5));
            var weeklyHistoryChanged = RecordWindowHistory(_weeklyHistory, snapshot.Secondary, snapshot.UpdatedAt, now, now - TimeSpan.FromDays(7));
            if (weeklyHistoryChanged)
            {
                _weeklyHistoryStore.Save(_weeklyHistory);
            }

            if (_adaptiveWeeklyForecastEnabled && (weeklyHistoryChanged || _adaptiveWeeklyForecastNeedsBaseline))
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

    private async Task ExecuteRefreshAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        CodexUsageSnapshot? tokenSnapshot = null;
        try
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && TryPublish(snapshot))
            {
                RecordHistory(snapshot);
                tokenSnapshot = snapshot;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            TraceFailure("unexpected refresh", error);
            var unavailable = CreateUnavailableSnapshot();
            if (TryPublish(unavailable))
            {
                RecordHistory(unavailable);
            }
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
            }
            completion.TrySetResult();
        }
    }

    private async Task<CodexUsageSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _appServerReader(cancellationToken).ConfigureAwait(false);
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
                var fallback = await Task.Run(() => _localSessionReader(cancellationToken), cancellationToken).ConfigureAwait(false);
                return fallback with { Error = LiveDataUnavailableMessage };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception fallbackError)
            {
                TraceFailure("local session fallback", fallbackError);
                return CreateUnavailableSnapshot();
            }
        }
    }

    private async Task<LocalTokenUsageSnapshot> ReadTokenUsageAsync(
        CodexUsageSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Secondary is not { WindowMinutes: > 0 } weekly)
        {
            return LocalTokenUsageSnapshot.Unavailable with { UpdatedAt = DateTimeOffset.Now };
        }

        var windowStart = weekly.ResetsAt - TimeSpan.FromMinutes(weekly.WindowMinutes);
        var windowEnd = DateTimeOffset.Now < weekly.ResetsAt ? DateTimeOffset.Now : weekly.ResetsAt;
        try
        {
            return await _localTokenUsageReader!(windowStart, windowEnd, TimeZoneInfo.Local, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            TraceFailure("local token usage read", error);
            return LocalTokenUsageSnapshot.Unavailable with { UpdatedAt = DateTimeOffset.Now };
        }
    }

    private void StartTokenRefresh(CodexUsageSnapshot snapshot)
    {
        lock (_refreshStateLock)
        {
            if (_disposed || _localTokenUsageReader is null || snapshot.Secondary is null
                || _tokenReadTask is { IsCompleted: false })
            {
                return;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(20));
            // Keep the actual read task, even after a timeout, so a non-cooperative
            // file read cannot cause overlapping scans of the reader's mutable cache.
            var generation = _tokenGeneration;
            var read = _tokenReadTask = Task.Run(() => ReadTokenUsageAsync(snapshot, cancellation.Token));
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

    private bool TryPublish(CodexUsageSnapshot snapshot)
    {
        lock (_refreshStateLock)
        {
            if (_disposed)
            {
                return false;
            }

            if (Current.Secondary?.ResetsAt != snapshot.Secondary?.ResetsAt
                || snapshot.Source == UsageDataSource.Unavailable)
            {
                CurrentTokenUsage = LocalTokenUsageSnapshot.Unavailable;
            }

            Current = snapshot;
            _tokenGeneration++;
            return true;
        }
    }

    private static CodexUsageSnapshot CreateUnavailableSnapshot() => CodexUsageSnapshot.Loading with
    {
        UpdatedAt = DateTimeOffset.Now,
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
            refreshTask = Task.WhenAll(_refreshTask ?? Task.CompletedTask, _tokenRefreshTask);
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
