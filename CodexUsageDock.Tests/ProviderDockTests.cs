using System.Globalization;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class ProviderDockTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    public void Dispose() => _environment.Dispose();

    [Fact]
    public async Task TogglingAlertsOffAndOnWithoutARefreshEstablishesANewBaseline()
    {
        File.WriteAllText(_environment.PathFor("settings.json"), """{"enableUsageAlerts":"true"}""");
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(4);
        var remaining = 40.0;
        CodexUsageSnapshot Snapshot() => new(new(100 - remaining, 300, reset), null, null, null, null,
            now, UsageDataSource.AppServer, null, AccountKey: "a", DefaultBucketId: "codex");
        using var service = _environment.CreateService(_ => Task.FromResult(Snapshot()), Snapshot, clock: () => now);
        var settings = _environment.CreateSettings();
        var messages = new List<string>();
        using var provider = new CodexUsageDockCommandsProvider(service, settings, messages.Add, () => now);
        await service.RefreshAsync();
        settings.GetContent().OfType<FormContent>().Last().SubmitForm("""{"enableUsageAlerts":"false"}""", "{}");
        settings.GetContent().OfType<FormContent>().Last().SubmitForm("""{"enableUsageAlerts":"true"}""", "{}");
        remaining = 5;
        now = now.AddMinutes(1);
        await service.RefreshAsync();
        Assert.Empty(messages);
        remaining = 30;
        now = now.AddMinutes(1);
        await service.RefreshAsync();
        remaining = 8;
        now = now.AddMinutes(1);
        await service.RefreshAsync();
        Assert.Single(messages);
    }

    [Fact]
    public void SeparateDockBandsHaveStableRestorableIdentities()
    {
        File.WriteAllText(_environment.PathFor("settings.json"), """{"separateDockItems":"true"}""");
        using var service = _environment.CreateService();
        using var provider = new CodexUsageDockCommandsProvider(service, _environment.CreateSettings(), _ => { });
        var bands = provider.GetDockBands()!;
        Assert.Equal(3, bands.Length);
        Assert.Equal(3, bands.Select(item => item.Command.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var band in bands)
        {
            Assert.Single(Assert.IsAssignableFrom<IListPage>(band.Command).GetItems());
            Assert.Same(band, provider.GetCommandItem(band.Command.Id));
        }
        Assert.Null(provider.GetCommandItem("unknown"));
        Assert.Null(provider.GetCommandItem(string.Empty));
        Assert.Null(provider.GetCommandItem(CombinedDockId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DockModeTransitionsKeepStableBandObjectsAndPersist(bool initiallySeparate)
    {
        File.WriteAllText(
            _environment.PathFor("settings.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [SeparateDockItemsKey] = initiallySeparate.ToString().ToLowerInvariant(),
            }));

        var initialId = initiallySeparate ? FiveHourDockId : CombinedDockId;
        var settings = _environment.CreateSettings();
        using (var service = _environment.CreateService())
        using (var provider = new CodexUsageDockCommandsProvider(service, settings, _ => { }))
        {
            var initialBand = FindBand(provider, initialId);
            Assert.Same(initialBand, provider.GetCommandItem(initialId));

            SubmitSettings(settings,
                (SeparateDockItemsKey, (!initiallySeparate).ToString().ToLowerInvariant()));
            Assert.Equal(initiallySeparate ? 1 : 3, provider.GetDockBands()!.Length);

            SubmitSettings(settings,
                (SeparateDockItemsKey, initiallySeparate.ToString().ToLowerInvariant()));
            Assert.Same(initialBand, FindBand(provider, initialId));
            Assert.Same(initialBand, provider.GetCommandItem(initialId));
        }

        using var restartedService = _environment.CreateService();
        using var restartedProvider = new CodexUsageDockCommandsProvider(
            restartedService,
            _environment.CreateSettings(),
            _ => { });
        Assert.Equal(initiallySeparate ? 3 : 1, restartedProvider.GetDockBands()!.Length);
        Assert.Same(
            FindBand(restartedProvider, initialId),
            restartedProvider.GetCommandItem(initialId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DockModeSwitchPersistsTheOppositeModeAcrossRestart(bool initiallySeparate)
    {
        File.WriteAllText(
            _environment.PathFor("settings.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [SeparateDockItemsKey] = initiallySeparate.ToString().ToLowerInvariant(),
            }));

        var settings = _environment.CreateSettings();
        var oldIds = initiallySeparate
            ? new[] { FiveHourDockId, WeeklyDockId, CreditsDockId }
            : new[] { CombinedDockId };
        var newIds = initiallySeparate
            ? new[] { CombinedDockId }
            : new[] { FiveHourDockId, WeeklyDockId, CreditsDockId };
        using (var service = _environment.CreateService())
        using (var provider = new CodexUsageDockCommandsProvider(service, settings, _ => { }))
        {
            SubmitSettings(settings, (SeparateDockItemsKey, (!initiallySeparate).ToString().ToLowerInvariant()));
            foreach (var id in oldIds)
            {
                Assert.Null(provider.GetCommandItem(id));
            }
        }

        using var restartedService = _environment.CreateService();
        using var restartedProvider = new CodexUsageDockCommandsProvider(
            restartedService,
            _environment.CreateSettings(),
            _ => { });
        Assert.Equal(newIds, restartedProvider.GetDockBands()!.Select(band => band.Command.Id));
        foreach (var id in oldIds)
        {
            Assert.Null(restartedProvider.GetCommandItem(id));
        }
        foreach (var id in newIds)
        {
            Assert.Same(FindBand(restartedProvider, id), restartedProvider.GetCommandItem(id));
        }
    }

    [Fact]
    public void RestorableLookupUsesOnlyTheActiveDockSelection()
    {
        File.WriteAllText(_environment.PathFor("settings.json"), $"{{\"{SeparateDockItemsKey}\":\"true\"}}");
        using var service = _environment.CreateService();
        var settings = _environment.CreateSettings();
        using var provider = new CodexUsageDockCommandsProvider(service, settings, _ => { });

        foreach (var band in provider.GetDockBands()!)
        {
            Assert.Same(band, provider.GetCommandItem(band.Command.Id));
        }

        Assert.Null(provider.GetCommandItem(CombinedDockId));
        SubmitSettings(settings, (SeparateDockItemsKey, "false"));

        var combined = FindBand(provider, CombinedDockId);
        Assert.Same(combined, provider.GetCommandItem(CombinedDockId));
        Assert.Null(provider.GetCommandItem(FiveHourDockId));
        Assert.Null(provider.GetCommandItem(WeeklyDockId));
        Assert.Null(provider.GetCommandItem(CreditsDockId));
    }

    [Fact]
    public void HiddenAndDisabledDockIdsAreNotRestorableInBothModes()
    {
        using var service = _environment.CreateService();
        var settings = _environment.CreateSettings();
        using var provider = new CodexUsageDockCommandsProvider(service, settings, _ => { });

        Assert.NotNull(provider.GetCommandItem(CombinedDockId));
        Assert.Null(provider.GetCommandItem(FiveHourDockId));
        Assert.Null(provider.GetCommandItem(ClaudeDockId));

        SubmitSettings(settings, (ShowFiveHourLimitKey, "false"));
        Assert.Null(provider.GetCommandItem(FiveHourDockId));
        Assert.Equal(2, Assert.IsAssignableFrom<IListPage>(FindBand(provider, CombinedDockId).Command).GetItems().Length);

        SubmitSettings(settings,
            (ShowWeeklyLimitKey, "false"),
            (ShowResetsAndCreditsKey, "false"));
        Assert.Empty(provider.GetDockBands()!);
        foreach (var id in AllDockIds)
        {
            Assert.Null(provider.GetCommandItem(id));
        }

        SubmitSettings(settings, (SeparateDockItemsKey, "true"), (ShowWeeklyLimitKey, "true"));
        Assert.Single(provider.GetDockBands()!);
        Assert.Null(provider.GetCommandItem(CombinedDockId));
        Assert.Null(provider.GetCommandItem(FiveHourDockId));
        Assert.Same(
            FindBand(provider, WeeklyDockId),
            provider.GetCommandItem(WeeklyDockId));
        Assert.Null(provider.GetCommandItem(CreditsDockId));
        Assert.Null(provider.GetCommandItem(ClaudeDockId));
        Assert.Null(provider.GetCommandItem("testhost-owned-pin"));

        SubmitSettings(settings, (ShowWeeklyLimitKey, "false"));
        Assert.Empty(provider.GetDockBands()!);
        Assert.Null(provider.GetCommandItem(WeeklyDockId));
    }

    [Fact]
    public void RetainedBandPagesAreClearedWhenTheirBandBecomesInactive()
    {
        using var service = _environment.CreateService();
        var settings = _environment.CreateSettings();
        using var provider = new CodexUsageDockCommandsProvider(service, settings, _ => { });

        var combinedBand = FindBand(provider, CombinedDockId);
        var combinedPage = Assert.IsAssignableFrom<IListPage>(combinedBand.Command);
        Assert.Equal(3, combinedPage.GetItems().Length);
        var combinedEmptyNotifications = 0;
        combinedPage.ItemsChanged += (_, _) =>
        {
            if (combinedPage.GetItems().Length == 0)
            {
                combinedEmptyNotifications++;
            }
        };

        SubmitSettings(settings,
            (ShowFiveHourLimitKey, "false"),
            (ShowWeeklyLimitKey, "false"),
            (ShowResetsAndCreditsKey, "false"));
        Assert.Empty(combinedPage.GetItems());
        Assert.True(combinedEmptyNotifications > 0);
        Assert.Empty(provider.GetDockBands()!);

        SubmitSettings(settings, (ShowWeeklyLimitKey, "true"));
        Assert.Same(combinedBand, FindBand(provider, CombinedDockId));
        Assert.Single(combinedPage.GetItems());

        var emptyNotificationsBeforeSeparate = combinedEmptyNotifications;
        SubmitSettings(settings, (SeparateDockItemsKey, "true"));
        Assert.Empty(combinedPage.GetItems());
        Assert.True(combinedEmptyNotifications > emptyNotificationsBeforeSeparate);
        var weeklyBand = FindBand(provider, WeeklyDockId);
        var weeklyPage = Assert.IsAssignableFrom<IListPage>(weeklyBand.Command);
        Assert.Single(weeklyPage.GetItems());
        var weeklyEmptyNotifications = 0;
        weeklyPage.ItemsChanged += (_, _) =>
        {
            if (weeklyPage.GetItems().Length == 0)
            {
                weeklyEmptyNotifications++;
            }
        };

        SubmitSettings(settings, (ShowWeeklyLimitKey, "false"));
        Assert.Empty(weeklyPage.GetItems());
        Assert.True(weeklyEmptyNotifications > 0);
        Assert.Empty(provider.GetDockBands()!);
    }

    [Fact]
    public void RetainedClaudeBandPageClearsAndNotifiesWhenClaudeIsDisabled()
    {
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var capture = WriteClaudeCapture(now, 25);
        File.WriteAllText(_environment.PathFor("settings.json"), JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [EnableClaudeKey] = "true",
                [ClaudeBridgePathKey] = capture,
            }));
        using var service = _environment.CreateService(
            _ => Task.FromResult(CodexUsageSnapshot.Loading),
            () => CodexUsageSnapshot.Loading,
            clock: () => now);
        var settings = _environment.CreateSettings();
        using var provider = new CodexUsageDockCommandsProvider(service, settings, _ => { }, () => now);

        var band = FindBand(provider, ClaudeDockId);
        var page = Assert.IsAssignableFrom<IListPage>(band.Command);
        Assert.Equal(2, page.GetItems().Length);
        var emptyNotifications = 0;
        page.ItemsChanged += (_, _) =>
        {
            if (page.GetItems().Length == 0)
            {
                emptyNotifications++;
            }
        };

        SubmitSettings(settings, (EnableClaudeKey, "false"));

        Assert.Empty(page.GetItems());
        Assert.True(emptyNotifications > 0);
        Assert.Null(provider.GetCommandItem(ClaudeDockId));
    }

    [Fact]
    public async Task QuotaRefreshKeepsBandIdentityAndNotifiesOnlyItsBandList()
    {
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var pending = new TaskCompletionSource<CodexUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = CodexUsageSnapshot.Loading with
        {
            Primary = new RateLimitWindow(25, 300, now.AddHours(4)),
            Secondary = new RateLimitWindow(40, 10080, now.AddDays(5)),
            UpdatedAt = now,
            Source = UsageDataSource.AppServer,
            Error = null,
            AccountKey = "test-account",
            DefaultBucketId = "codex",
        };
        using var service = _environment.CreateService(
            _ => pending.Task,
            () => CodexUsageSnapshot.Loading,
            clock: () => now);
        using var provider = new CodexUsageDockCommandsProvider(
            service,
            _environment.CreateSettings(),
            _ => { },
            () => now);
        var band = FindBand(provider, CombinedDockId);
        var list = Assert.IsAssignableFrom<IListPage>(band.Command);
        var providerInvalidations = 0;
        var bandInvalidations = 0;
        provider.ItemsChanged += (_, _) => providerInvalidations++;
        list.ItemsChanged += (_, _) => bandInvalidations++;

        var refresh = service.RefreshAsync();
        pending.SetResult(snapshot);
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(band, FindBand(provider, CombinedDockId));
        Assert.Same(band, provider.GetCommandItem(CombinedDockId));
        Assert.Equal(0, providerInvalidations);
        Assert.True(bandInvalidations > 0);
    }

    [Fact]
    public async Task RepeatedCompletedQuotaRefreshNotifiesAPreviouslyReadItem()
    {
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var firstRead = new TaskCompletionSource<CodexUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource<CodexUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readNumber = 0;
        var snapshot = CodexUsageSnapshot.Loading with
        {
            Primary = new RateLimitWindow(25, 300, now.AddHours(4)),
            Secondary = new RateLimitWindow(40, 10080, now.AddDays(5)),
            UpdatedAt = now,
            Source = UsageDataSource.AppServer,
            Error = null,
            AccountKey = "test-account",
            DefaultBucketId = "codex",
            ResetCredits = new RateLimitResetCredits(2, null),
        };
        Task<CodexUsageSnapshot> Read(CancellationToken _) =>
            Interlocked.Increment(ref readNumber) == 1 ? firstRead.Task : secondRead.Task;
        using var service = _environment.CreateService(
            Read,
            () => CodexUsageSnapshot.Loading,
            clock: () => now);
        using var provider = new CodexUsageDockCommandsProvider(
            service,
            _environment.CreateSettings(),
            _ => { },
            () => now);

        var band = FindBand(provider, CombinedDockId);
        // Reset-credit text is independent of the real-time window validity clock.
        var item = Assert.IsAssignableFrom<IListPage>(band.Command).GetItems()[2];
        var cachedTitle = item.Title;
        var firstRefresh = service.RefreshAsync();
        firstRead.SetResult(snapshot);
        await firstRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(item.Title, cachedTitle);
        item.PropChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ICommandItem.Title)) cachedTitle = item.Title;
        };

        var secondRefresh = service.RefreshAsync();
        secondRead.SetResult(snapshot);
        await secondRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(item.Title, cachedTitle);
    }

    [Fact]
    public async Task ClaudeRefreshKeepsBandIdentityAndNotifiesOnlyItsBandList()
    {
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var firstCapture = WriteClaudeCapture(now, 25);
        var secondCapture = WriteClaudeCapture(now, 35);
        File.WriteAllText(_environment.PathFor("settings.json"), JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                [EnableClaudeKey] = "true",
                [ClaudeBridgePathKey] = firstCapture,
            }));
        var pending = new TaskCompletionSource<CodexUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = _environment.CreateService(
            _ => pending.Task,
            () => CodexUsageSnapshot.Loading,
            clock: () => now);
        using var provider = new CodexUsageDockCommandsProvider(
            service,
            _environment.CreateSettings(),
            _ => { },
            () => now);
        await service.ClaudeRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));

        var codexBand = FindBand(provider, CombinedDockId);
        var claudeBand = FindBand(provider, ClaudeDockId);
        var claudeList = Assert.IsAssignableFrom<IListPage>(claudeBand.Command);
        var providerInvalidations = 0;
        var claudeInvalidations = 0;
        provider.ItemsChanged += (_, _) => providerInvalidations++;
        claudeList.ItemsChanged += (_, _) => claudeInvalidations++;

        service.ConfigureClaude(false, firstCapture);
        service.ConfigureClaude(true, secondCapture);
        await service.ClaudeRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(codexBand, FindBand(provider, CombinedDockId));
        Assert.Same(claudeBand, FindBand(provider, ClaudeDockId));
        Assert.Same(claudeBand, provider.GetCommandItem(ClaudeDockId));
        Assert.Equal(0, providerInvalidations);
        Assert.True(claudeInvalidations > 0);

        pending.TrySetResult(CodexUsageSnapshot.Loading);
    }

    private const string CombinedDockId = "nl.mathijs.codexusage.dock";
    private const string FiveHourDockId = "nl.mathijs.codexusage.dock.five-hour";
    private const string WeeklyDockId = "nl.mathijs.codexusage.dock.weekly";
    private const string CreditsDockId = "nl.mathijs.codexusage.dock.credits";
    private const string ClaudeDockId = "nl.mathijs.codexusage.dock.claude";
    private const string SeparateDockItemsKey = "separateDockItems";
    private const string ShowFiveHourLimitKey = "showFiveHourLimit";
    private const string ShowWeeklyLimitKey = "showWeeklyLimit";
    private const string ShowResetsAndCreditsKey = "showResetsAndCredits";
    private const string EnableClaudeKey = "enableClaude";
    private const string ClaudeBridgePathKey = "claudeBridgePath";
    private static readonly string[] AllDockIds = [
        CombinedDockId,
        FiveHourDockId,
        WeeklyDockId,
        CreditsDockId,
        ClaudeDockId,
    ];

    private static ICommandItem FindBand(CodexUsageDockCommandsProvider provider, string id) =>
        Assert.Single(provider.GetDockBands() ?? Array.Empty<ICommandItem>(), item => item.Command.Id == id);

    private static void SubmitSettings(
        CodexUsageDockSettingsPage page,
        params (string Key, string Value)[] values)
    {
        var payload = values.ToDictionary(pair => pair.Key, pair => pair.Value);
        page.GetContent().OfType<FormContent>().Last().SubmitForm(JsonSerializer.Serialize(payload), "{}");
    }

    private string WriteClaudeCapture(DateTimeOffset now, int fiveHourUsed)
    {
        var path = _environment.PathFor($"claude-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            provider = "claude",
            observedAtUTC = now.ToString("O", CultureInfo.InvariantCulture),
            rate_limits = new
            {
                five_hour = new
                {
                    used_percentage = fiveHourUsed,
                    resets_at = now.AddHours(4).ToUnixTimeSeconds(),
                },
                seven_day = new
                {
                    used_percentage = 40,
                    resets_at = now.AddDays(4).ToUnixTimeSeconds(),
                },
            },
        }));
        return path;
    }
}
