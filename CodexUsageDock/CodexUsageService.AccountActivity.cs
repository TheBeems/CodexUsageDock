namespace CodexUsageDock;

internal sealed partial class CodexUsageService
{
    private readonly Func<CancellationToken, Task<AccountUsageSnapshot>>? _accountUsageReader;
    private Task<AccountUsageSnapshot>? _accountReadTask;
    private Task _accountRefreshTask = Task.CompletedTask;
    private DateTimeOffset _accountReadAfter;
    private bool _accountActivityEnabled = true;

    internal AccountUsageSnapshot CurrentAccountUsage { get; private set; } = AccountUsageSnapshot.Unavailable;

    internal Task AccountUsageRefreshTask
    {
        get { lock (_refreshStateLock) { return _accountRefreshTask; } }
    }

    internal void RequestAccountUsageRefresh()
    {
        lock (_refreshStateLock) { _accountReadAfter = DateTimeOffset.MinValue; }
    }

    internal void SetAccountActivityEnabled(bool enabled)
    {
        lock (_refreshStateLock)
        {
            if (_accountActivityEnabled == enabled) return;
            _accountActivityEnabled = enabled;
            _accountReadAfter = DateTimeOffset.MinValue;
            if (!enabled) CurrentAccountUsage = AccountUsageSnapshot.Unavailable;
        }
        RaiseUpdated();
    }

    private void StartAccountUsageRefresh(CodexUsageSnapshot snapshot)
    {
        lock (_refreshStateLock)
        {
            if (_disposed || !ReferenceEquals(Current, snapshot) || !_accountActivityEnabled || (!_usesConfiguredSources && _accountUsageReader is null)
                || snapshot.Source != UsageDataSource.AppServer || snapshot.AccountKey is null
                || _accountReadTask is { IsCompleted: false } || _clock() < _accountReadAfter)
            {
                return;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(25));
            var sourceGeneration = _sourceGeneration;
            var options = _sourceOptions;
            var account = snapshot.AccountKey;
            _accountReadAfter = _clock().AddMinutes(5);
            var read = _accountReadTask = Task.Run(() => _usesConfiguredSources
                ? CodexAppServerReader.ReadAccountUsageAsync(options, cancellation.Token)
                : _accountUsageReader!(cancellation.Token));
            _accountRefreshTask = Task.Run(() => ObserveAccountUsageAsync(read, cancellation, sourceGeneration, account));
        }
    }

    private async Task ObserveAccountUsageAsync(Task<AccountUsageSnapshot> read, CancellationTokenSource cancellation,
        long sourceGeneration, string account)
    {
        try
        {
            var result = await read.WaitAsync(cancellation.Token).ConfigureAwait(false);
            lock (_refreshStateLock)
            {
                if (_disposed || !_accountActivityEnabled || sourceGeneration != _sourceGeneration
                    || !string.Equals(Current.AccountKey, account, StringComparison.Ordinal)) return;
                if (result.Status is AccountUsageStatus.Available or AccountUsageStatus.Partial
                    && !string.Equals(result.AccountKey, account, StringComparison.Ordinal)) return;

                CurrentAccountUsage = result;
                if (result.Status == AccountUsageStatus.Unsupported) _accountReadAfter = _clock().AddMinutes(30);
            }
            RaiseUpdated();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LocalStorage.TraceFailure("read account activity", exception);
        }
        finally
        {
            if (read.IsCompleted) cancellation.Dispose();
            else _ = read.ContinueWith(task =>
            {
                _ = task.Exception;
                cancellation.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
