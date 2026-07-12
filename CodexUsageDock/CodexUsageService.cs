namespace CodexUsageDock;

internal sealed class CodexUsageService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly System.Timers.Timer _timer = new(RefreshInterval.TotalMilliseconds) { AutoReset = true };
    private readonly object _historyLock = new();
    private readonly List<UsageHistoryEntry> _primaryHistory = [];
    private bool _disposed;

    public CodexUsageSnapshot Current { get; private set; } = CodexUsageSnapshot.Loading;

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
        _timer.Elapsed += OnTimer;
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || !await _refreshLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            try
            {
                Current = await CodexAppServerReader.ReadAsync().ConfigureAwait(false);
            }
            catch (Exception appServerError)
            {
                var fallback = await Task.Run(LocalCodexSessionReader.ReadLatest).ConfigureAwait(false);
                Current = fallback with { Error = $"App-server: {ShortError(appServerError)}" };
            }

            RecordHistory(Current);
            Updated?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
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

    private static string ShortError(Exception error)
    {
        var message = error.GetBaseException().Message.Replace(Environment.NewLine, " ", StringComparison.Ordinal);
        return message.Length <= 120 ? message : message[..117] + "...";
    }

    private void OnTimer(object? sender, System.Timers.ElapsedEventArgs e) => _ = RefreshAsync();

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Elapsed -= OnTimer;
        _timer.Dispose();
        _refreshLock.Dispose();
    }
}
