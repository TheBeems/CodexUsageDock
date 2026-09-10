namespace CodexUsageDock;

internal sealed partial class CodexUsageService
{
    private bool _claudeEnabled;
    private string _claudePath = string.Empty;
    private TimeSpan _claudeRefreshInterval;
    private long _claudeGeneration;
    private Task? _claudeReadTask;
    private ClaudeUsageSnapshot _claudeUsage = ClaudeUsageSnapshot.Unavailable("The Claude pilot is disabled.");
    internal event EventHandler? ClaudeUpdated;

    internal ClaudeUsageSnapshot GetClaudeUsage() { lock (_refreshStateLock) { return _claudeUsage; } }
    internal Task ClaudeRefreshTask { get { lock (_refreshStateLock) { return _claudeReadTask ?? Task.CompletedTask; } } }

    internal void ConfigureClaude(bool enabled, string path)
    {
        lock (_refreshStateLock)
        {
            var interval = RefreshInterval;
            if (_disposed || _claudeEnabled == enabled && _claudePath == path && _claudeRefreshInterval == interval) return;
            _claudeEnabled = enabled;
            _claudePath = path;
            _claudeRefreshInterval = interval;
            _claudeGeneration++;
            _claudeUsage = ClaudeUsageSnapshot.Unavailable(enabled ? "Waiting for a local Claude usage capture." : "The Claude pilot is disabled.");
        }
        RaiseClaudeUpdated();
        StartClaudeRefresh();
    }

    private void StartClaudeRefresh()
    {
        lock (_refreshStateLock)
        {
            if (_disposed || !_claudeEnabled || _claudeReadTask is { IsCompleted: false }) return;
            var path = _claudePath;
            var generation = _claudeGeneration;
            var interval = RefreshInterval;
            var cancellationToken = _lifetimeCancellation.Token;
            _claudeReadTask = Task.Run(() =>
            {
                ClaudeUsageSnapshot result;
                try { result = ClaudeUsageReader.Read(path, _clock(), interval, cancellationToken); }
                catch (Exception error)
                {
                    if (error is not OperationCanceledException) LocalStorage.TraceFailure("read Claude capture", error);
                    result = ClaudeUsageSnapshot.Unavailable("The Claude usage capture could not be read.");
                }
                lock (_refreshStateLock)
                {
                    if (!_disposed && generation == _claudeGeneration && _claudeEnabled) _claudeUsage = result;
                }
                RaiseClaudeUpdated();
                bool restart;
                lock (_refreshStateLock)
                {
                    _claudeReadTask = null;
                    restart = !_disposed && generation != _claudeGeneration && _claudeEnabled;
                }
                if (restart) StartClaudeRefresh();
            });
        }
    }

    private void RaiseClaudeUpdated()
    {
        lock (_refreshStateLock) { if (_disposed) return; }
        try { ClaudeUpdated?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) { LocalStorage.TraceFailure("update Claude presentation", error); }
    }
}
