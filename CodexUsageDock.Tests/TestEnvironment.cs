namespace CodexUsageDock.Tests;

internal sealed class TestEnvironment : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexUsageDock.Tests", Guid.NewGuid().ToString("N"));

    internal string PathFor(string name)
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, name);
    }

    internal CodexUsageDockSettingsPage CreateSettings() => new(PathFor("settings.json"));

    internal CodexUsageService CreateService() => CreateService(
        _ => Task.FromResult(CodexUsageSnapshot.Loading), _ => CodexUsageSnapshot.Loading);

    internal CodexUsageService CreateService(
        Func<CancellationToken, Task<CodexUsageSnapshot>> appServerReader,
        Func<CancellationToken, CodexUsageSnapshot> localSessionReader,
        WeeklyUsageHistoryStore? weeklyHistoryStore = null,
        AdaptiveWeeklyUsageStore? adaptiveWeeklyUsageStore = null,
        Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>>? localTokenUsageReader = null,
        Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<AccountUsageSnapshot>>? accountUsageReader = null) =>
        new(appServerReader, localSessionReader,
            weeklyHistoryStore ?? new WeeklyUsageHistoryStore(PathFor("weekly.json")),
            adaptiveWeeklyUsageStore ?? new AdaptiveWeeklyUsageStore(PathFor("adaptive.json")),
            localTokenUsageReader, clock, accountUsageReader);

    internal CodexUsageService CreateService(
        Func<CancellationToken, Task<CodexUsageSnapshot>> appServerReader,
        Func<CodexUsageSnapshot> localSessionReader,
        WeeklyUsageHistoryStore? weeklyHistoryStore = null,
        AdaptiveWeeklyUsageStore? adaptiveWeeklyUsageStore = null,
        Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>>? localTokenUsageReader = null,
        Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<AccountUsageSnapshot>>? accountUsageReader = null) =>
        CreateService(appServerReader, _ => localSessionReader(), weeklyHistoryStore, adaptiveWeeklyUsageStore, localTokenUsageReader, clock, accountUsageReader);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
