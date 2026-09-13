namespace CodexUsageDock;

internal sealed partial class CodexUsageService
{
    private string? _aggregatePath;
    private UsageAggregateStore? _aggregateStore;
    private int _aggregateRetentionDays;

    internal void SetAggregateRetentionDays(int days)
    {
        if (days is not (0 or 7 or 30 or 90)) throw new ArgumentOutOfRangeException(nameof(days));
        lock (_historyLock)
        {
            if (_aggregateRetentionDays == days) return;
            _aggregateRetentionDays = days;
            OpenAggregateStore();
            if (days != 0) _aggregateStore?.SetRetentionDays(days, _clock());
        }
        RaiseUpdated();
    }

    private void OpenAggregateStore() => _aggregateStore = _aggregatePath is not null && _historyContext is not null
        ? new UsageAggregateStore(LocalStorage.ContextPath(_aggregatePath, _historyContext),
            _aggregateRetentionDays == 0 ? 90 : _aggregateRetentionDays, _clock)
        : null;

    private void RecordAggregate(CodexUsageSnapshot snapshot, DateTimeOffset now)
    {
        if (_aggregateRetentionDays == 0 || _aggregateStore is null) return;
        if (UsageAggregateStore.TryCreateLivePoint(snapshot, now, RefreshInterval, out var point))
            _aggregateStore.Record(point!, now);
    }

    internal (IReadOnlyList<UsageAggregatePoint> Points, int RetentionDays, string? Error, bool Identified, string? Context) GetAggregateHistory()
    {
        lock (_historyLock)
        {
            return (_aggregateStore?.Snapshot ?? [], _aggregateRetentionDays, _aggregateStore?.StorageError, _historyContext is not null, _historyContext);
        }
    }

    internal bool ClearAggregateHistory(string? expectedContext = null)
    {
        bool cleared;
        lock (_historyLock)
        {
            if (expectedContext is not null && expectedContext != _historyContext) return false;
            cleared = _aggregateStore?.Clear() ?? _historyContext is not null;
        }
        RaiseUpdated();
        return cleared;
    }

    internal string ExportAggregateHistory(bool csv, string? expectedContext = null)
    {
        lock (_historyLock)
        {
            if (expectedContext is not null && expectedContext != _historyContext)
                return "The account or quota category changed. Open its history and request the export again.";
            if (_aggregateStore is null || _aggregatePath is null)
                return "Identify the current Codex account before exporting its history.";
            var content = csv ? _aggregateStore.ExportCsv(_clock()) : _aggregateStore.ExportJson(_clock());
            var file = $"codex-usage-{_clock():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.{(csv ? "csv" : "json")}";
            var destination = Path.Combine(Path.GetDirectoryName(_aggregatePath)!, "exports", file);
            return LocalStorage.TryWrite(destination, content) ? $"Export saved to {destination}" : "The export could not be saved.";
        }
    }
}
