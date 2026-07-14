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
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Func<CancellationToken, Task<CodexUsageSnapshot>> _appServerReader;
    private readonly Func<CancellationToken, CodexUsageSnapshot> _localSessionReader;
    private Task? _refreshTask;
    private bool _disposed;
    private bool _isLoading;
    private bool _started;

    public CodexUsageService()
        : this(CodexAppServerReader.ReadAsync, LocalCodexSessionReader.ReadLatest)
    {
    }

    internal CodexUsageService(
        Func<CancellationToken, Task<CodexUsageSnapshot>> appServerReader,
        Func<CancellationToken, CodexUsageSnapshot> localSessionReader)
    {
        _appServerReader = appServerReader;
        _localSessionReader = localSessionReader;
    }

    internal CodexUsageService(
        Func<CancellationToken, Task<CodexUsageSnapshot>> appServerReader,
        Func<CodexUsageSnapshot> localSessionReader)
        : this(appServerReader, _ => localSessionReader())
    {
    }

    public CodexUsageSnapshot Current { get; private set; } = CodexUsageSnapshot.Loading;

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

    public Task RefreshAsync()
    {
        TaskCompletionSource completion;
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
        }

        RaiseUpdated();
        _ = ExecuteRefreshAsync(completion, _lifetimeCancellation.Token);
        return completion.Task;
    }

    internal void RecordHistory(CodexUsageSnapshot snapshot, DateTimeOffset? recordedAt = null)
    {
        lock (_historyLock)
        {
            var now = recordedAt ?? DateTimeOffset.Now;
            var cutoff = now - TimeSpan.FromHours(5);
            _primaryHistory.RemoveAll(entry => entry.RecordedAt < cutoff);
            if (snapshot.Primary is null || snapshot.UpdatedAt < cutoff)
            {
                return;
            }

            if (_primaryHistory.Count > 0 && snapshot.UpdatedAt <= _primaryHistory[^1].RecordedAt)
            {
                return;
            }

            _primaryHistory.Add(new UsageHistoryEntry(snapshot.UpdatedAt, snapshot.Primary.RemainingPercent));
        }
    }

    private async Task ExecuteRefreshAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested && TryPublish(snapshot))
            {
                RecordHistory(snapshot);
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

    private bool TryPublish(CodexUsageSnapshot snapshot)
    {
        lock (_refreshStateLock)
        {
            if (_disposed)
            {
                return false;
            }

            Current = snapshot;
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
            refreshTask = _refreshTask;
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
