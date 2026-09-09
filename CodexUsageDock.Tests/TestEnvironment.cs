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
        Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>>? localTokenUsageReader = null) =>
        new(appServerReader, localSessionReader,
            weeklyHistoryStore ?? new WeeklyUsageHistoryStore(PathFor("weekly.json")),
            adaptiveWeeklyUsageStore ?? new AdaptiveWeeklyUsageStore(PathFor("adaptive.json")),
            localTokenUsageReader);

    internal CodexUsageService CreateService(
        Func<CancellationToken, Task<CodexUsageSnapshot>> appServerReader,
        Func<CodexUsageSnapshot> localSessionReader,
        WeeklyUsageHistoryStore? weeklyHistoryStore = null,
        AdaptiveWeeklyUsageStore? adaptiveWeeklyUsageStore = null,
        Func<DateTimeOffset, DateTimeOffset, TimeZoneInfo, CancellationToken, Task<LocalTokenUsageSnapshot>>? localTokenUsageReader = null) =>
        CreateService(appServerReader, _ => localSessionReader(), weeklyHistoryStore, adaptiveWeeklyUsageStore, localTokenUsageReader);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
