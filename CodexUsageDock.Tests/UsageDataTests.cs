using System.Text.Json;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;
namespace CodexUsageDock.Tests;

public sealed class UsageDataTests
{
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void SettingsDefaultToShowingAllDockUsageInformation()
    {
        var settings = new CodexUsageDockSettingsPage();

        Assert.True(settings.ShowFiveHourLimit);
        Assert.True(settings.ShowWeeklyLimit);
        Assert.True(settings.ShowResetsAndCredits);
        Assert.True(settings.ShowResetTime);
        Assert.Equal(TimeSpan.FromMinutes(1), settings.RefreshInterval);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("15", 15)]
    [InlineData("unexpected", 1)]
    public void RefreshIntervalUsesOnlySupportedValues(string value, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), CodexUsageDockSettingsPage.ParseRefreshInterval(value));
    }

    [Fact]
    public void ClassifyWindows_RecognizesWeeklyWindowWhenItIsPrimary()
    {
        var weekly = new RateLimitWindow(3, 10080, DateTimeOffset.Now.AddDays(7));

        var result = RateLimitWindowParser.Classify(weekly, null);

        Assert.Null(result.FiveHour);
        Assert.Same(weekly, result.Weekly);
    }

    [Fact]
    public void MissingKnownWindowsAreRejectedForAppServerData()
    {
        Assert.Throws<InvalidOperationException>(() => RateLimitWindowParser.ThrowIfNoKnownWindow(default));

        var weekly = new RateLimitWindow(3, 10080, DateTimeOffset.Now.AddDays(7));
        RateLimitWindowParser.ThrowIfNoKnownWindow(new ClassifiedRateLimitWindows(null, weekly));
    }

    [Fact]
    public void Summary_UsesWeeklyWindowWhenFiveHourWindowIsInactive()
    {
        var now = new DateTimeOffset(2026, 7, 12, 14, 0, 0, TimeSpan.Zero);
        var snapshot = CodexUsageSnapshot.Loading with
        {
            Secondary = new RateLimitWindow(3, 10080, now.AddDays(7)),
            UpdatedAt = now,
            Error = null,
        };

        var summary = CodexUsageDockPage.FormatSummary(snapshot, now);

        Assert.Contains("97% available", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage allowance unknown", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFiveHourWindow_CanBeDescribedAsInactive()
    {
        var text = CodexUsageDockPage.FormatWindow("5-hour window", null, DateTimeOffset.Now, "Currently inactive");

        Assert.Contains("Currently inactive", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Not available", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppServerWorkingDirectory_IsNeverInheritedFromSystem32ForBareCommand()
    {
        var workingDirectory = CodexAppServerReader.GetSafeWorkingDirectory("codex.exe");

        Assert.True(Path.IsPathRooted(workingDirectory));
        Assert.False(string.Equals(Environment.SystemDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AppServerReaderReturnsCancellationForPreCanceledToken()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CodexAppServerReader.ReadAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public void CliSelectionPrefersStandaloneCliAndRecognizesWindowsApps()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var standaloneCli = Path.Combine(temporaryDirectory, "codex.exe");
            File.WriteAllText(standaloneCli, string.Empty);
            var windowsAppsCli = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps",
                "OpenAI.Codex_test",
                "app",
                "resources",
                "codex.exe");

            var selected = CodexAppServerReader.SelectLaunchableCliPath([windowsAppsCli, standaloneCli]);

            Assert.Equal(standaloneCli, selected);
            Assert.True(CodexAppServerReader.IsWindowsAppsPath(windowsAppsCli));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void CliSelectionSupportsNpmCommandWrapper()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var commandWrapper = Path.Combine(temporaryDirectory, "codex.cmd");
            File.WriteAllText(commandWrapper, string.Empty);

            var selected = CodexAppServerReader.SelectLaunchableCliPath([commandWrapper]);

            Assert.Equal(commandWrapper, selected);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void FallbackMessageExplainsThatLiveDataIsUnavailableWithoutExposingDiagnostics()
    {
        var snapshot = CodexUsageSnapshot.Loading with
        {
            Source = UsageDataSource.LocalSession,
            Error = @"C:\Users\Alice\private-session.jsonl",
        };

        var message = CodexUsageDockPage.FormatError(snapshot);

        Assert.Contains("Live Codex data is unavailable", message, StringComparison.Ordinal);
        Assert.Contains("local fallback data", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsPageUsesTheProjectReleaseVersion()
    {
        using var service = new CodexUsageService();
        using var page = new CodexUsageDockPage(service, new CodexUsageDockSettingsPage());

        Assert.Equal("0.3.0", CodexUsageDockMetadata.Version);
        Assert.Equal($"Codex Usage - {CodexUsageDockMetadata.Version}", page.Title);
    }

    [Fact]
    public void DetailsPageContextMenuIncludesRefreshThenSettings()
    {
        using var service = new CodexUsageService();
        using var page = new CodexUsageDockPage(service, new CodexUsageDockSettingsPage());

        Assert.Collection(
            page.Commands,
            refresh => Assert.Equal("Refresh now", Assert.IsType<CommandContextItem>(refresh).Title),
            settings => Assert.Equal("Codex Usage settings", Assert.IsType<CommandContextItem>(settings).Title));
    }

    [Theory]
    [InlineData(true, "Refreshing Codex usage")]
    [InlineData(false, "")]
    public void RefreshStatusIsVisibleOnlyWhileLoading(bool isLoading, string expected)
    {
        var status = CodexUsageDockPage.FormatRefreshStatus(isLoading);

        if (isLoading)
        {
            Assert.Contains(expected, status, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(status);
        }
    }

    [Fact]
    public void ParseWindowSupportsAppServerAndRolloutSchemas()
    {
        using var appServer = JsonDocument.Parse("""{ "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1784246400 } }""");
        using var rollout = JsonDocument.Parse("""{ "primary": { "used_percent": 25, "window_minutes": 300, "resets_at": 1784246400 } }""");

        var appServerWindow = RateLimitWindowParser.TryParse(appServer.RootElement, "primary", "usedPercent", "windowDurationMins", "resetsAt");
        var rolloutWindow = RateLimitWindowParser.TryParse(rollout.RootElement, "primary", "used_percent", "window_minutes", "resets_at");

        Assert.Equal(appServerWindow, rolloutWindow);
        Assert.Equal(75, appServerWindow!.RemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784246400), appServerWindow.ResetsAt);
    }

    [Fact]
    public void ParseWindowRejectsMalformedData()
    {
        using var malformed = JsonDocument.Parse("""{ "primary": { "usedPercent": "25", "windowDurationMins": 300, "resetsAt": 1784246400 } }""");

        var window = RateLimitWindowParser.TryParse(malformed.RootElement, "primary", "usedPercent", "windowDurationMins", "resetsAt");

        Assert.Null(window);
    }

    [Fact]
    public async Task ReadResponsesPreservesOutOfOrderExpectedResponses()
    {
        using var output = new StringReader("""
            { "id": 3, "result": { "account": {} } }
            { "method": "codex/event" }
            { "id": 2, "result": { "rateLimits": {} } }
            """);

        var responses = await CodexAppServerReader.ReadResponsesAsync(output, CancellationToken.None, 2, 3);
        try
        {
            Assert.Equal(2, responses.Count);
            Assert.True(responses.ContainsKey(2));
            Assert.True(responses.ContainsKey(3));
        }
        finally
        {
            foreach (var response in responses.Values)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public void ParseResetCredits_UsesAvailableCountAndExpiryDetails()
    {
        using var json = JsonDocument.Parse("""
            {
              "rateLimitResetCredits": {
                "availableCount": 2,
                "credits": [
                  { "title": "Full reset", "status": "available", "expiresAt": 1784246400 }
                ]
              }
            }
            """);

        var resets = CodexAppServerReader.ParseResetCredits(json.RootElement);

        Assert.NotNull(resets);
        Assert.Equal(2, resets.AvailableCount);
        var credit = Assert.Single(resets.Credits!);
        Assert.Equal("Full reset", credit.Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784246400), credit.ExpiresAt);
    }

    [Fact]
    public void ParseResetCredits_PreservesCountWhenDetailsAreNull()
    {
        using var json = JsonDocument.Parse("""{ "rateLimitResetCredits": { "availableCount": 3, "credits": null } }""");

        var resets = CodexAppServerReader.ParseResetCredits(json.RootElement);

        Assert.Equal(3, resets!.AvailableCount);
        Assert.Null(resets.Credits);
    }

    [Fact]
    public void ParseResetCreditsPreservesCreditAndIgnoresOutOfRangeExpiry()
    {
        using var json = JsonDocument.Parse("""
            {
              "rateLimitResetCredits": {
                "availableCount": 1,
                "credits": [
                  { "title": "Full reset", "status": "available", "expiresAt": 9223372036854775807 }
                ]
              }
            }
            """);

        var resets = CodexAppServerReader.ParseResetCredits(json.RootElement);

        var credit = Assert.Single(resets!.Credits!);
        Assert.Equal("Full reset", credit.Title);
        Assert.Null(credit.ExpiresAt);
    }

    [Fact]
    public void ParseResetCreditsRejectsNegativeAvailableCount()
    {
        using var json = JsonDocument.Parse("""{ "rateLimitResetCredits": { "availableCount": -1, "credits": null } }""");

        var resets = CodexAppServerReader.ParseResetCredits(json.RootElement);

        Assert.Null(resets);
    }

    [Fact]
    public void ParseResetCreditsRejectsNonNumericAvailableCount()
    {
        using var json = JsonDocument.Parse("""{ "rateLimitResetCredits": { "availableCount": "2", "credits": null } }""");

        var resets = CodexAppServerReader.ParseResetCredits(json.RootElement);

        Assert.Null(resets);
    }

    [Fact]
    public void ParseResetCreditsIgnoresNonNumericExpiry()
    {
        using var json = JsonDocument.Parse("""
            {
              "rateLimitResetCredits": {
                "availableCount": 1,
                "credits": [
                  { "title": "Full reset", "status": "available", "expiresAt": "tomorrow" }
                ]
              }
            }
            """);

        var resets = CodexAppServerReader.ParseResetCredits(json.RootElement);

        var credit = Assert.Single(resets!.Credits!);
        Assert.Null(credit.ExpiresAt);
    }

    [Fact]
    public void RequestJsonPreservesMethodIdAndTypedParameters()
    {
        var json = CodexAppServerReader.CreateRequestJson(
            "account/read",
            3,
            static writer =>
            {
                writer.WritePropertyName("params");
                writer.WriteStartObject();
                writer.WriteBoolean("refreshToken", false);
                writer.WriteEndObject();
            });

        using var document = JsonDocument.Parse(json);
        Assert.Equal("account/read", document.RootElement.GetProperty("method").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("id").GetInt32());
        Assert.False(document.RootElement.GetProperty("params").GetProperty("refreshToken").GetBoolean());
    }

    [Fact]
    public void ParseCredits_HandlesBalanceAndUnlimitedAccounts()
    {
        using var result = JsonDocument.Parse("{}");
        using var balanceLimits = JsonDocument.Parse("""{ "credits": { "hasCredits": true, "unlimited": false, "balance": "10.00" } }""");
        using var unlimitedLimits = JsonDocument.Parse("""{ "credits": { "hasCredits": true, "unlimited": true, "balance": null } }""");

        var balance = CodexAppServerReader.ParseCredits(result.RootElement, balanceLimits.RootElement);
        var unlimited = CodexAppServerReader.ParseCredits(result.RootElement, unlimitedLimits.RootElement);

        Assert.Equal("10.00", balance!.Balance);
        Assert.True(unlimited!.Unlimited);
    }

    [Fact]
    public void ParseCreditsIgnoresStructuredBalanceValues()
    {
        using var result = JsonDocument.Parse("{}");
        using var limits = JsonDocument.Parse("""{ "credits": { "balance": { "unexpected": "value" } } }""");

        var credits = CodexAppServerReader.ParseCredits(result.RootElement, limits.RootElement);

        Assert.NotNull(credits);
        Assert.False(credits.HasCredits);
        Assert.Null(credits.Balance);
    }

    [Fact]
    public void ExternalUsageTextIsBoundedSingleLineAndMarkdownSafe()
    {
        var sanitized = UsageText.SanitizeExternal("  pro\r\n# heading\0  ", 32);
        var bounded = UsageText.SanitizeExternal(new string('x', 100), 32);

        Assert.Equal("pro # heading", sanitized);
        Assert.Equal(32, bounded!.Length);
        Assert.Equal(@"pro \# heading", UsageText.EscapeMarkdown(sanitized!));
        Assert.Equal(@"Pro \# Heading", CodexUsageDockPage.FormatPlan("pro\r\n# heading"));
    }

    [Theory]
    [InlineData(null, null, "↻ --")]
    [InlineData(0, null, "↻ 0")]
    [InlineData(2, "10.00", "↻ 2 · 10.00")]
    public void DockSummary_FormatsAvailableData(int? resetCount, string? balance, string expected)
    {
        var snapshot = new CodexUsageSnapshot(
            null,
            null,
            "pro",
            balance is null ? null : new CreditBalance(true, false, balance),
            resetCount is null ? null : new RateLimitResetCredits(resetCount.Value, null),
            DateTimeOffset.Now,
            UsageDataSource.Initializing,
            null);

        Assert.Equal(expected, UsageDockItem.FormatResetsAndCredits(snapshot));
    }

    [Theory]
    [InlineData(nameof(UsageDockItemKind.FiveHour), "5h --")]
    [InlineData(nameof(UsageDockItemKind.Weekly), "Week --")]
    [InlineData(nameof(UsageDockItemKind.ResetsAndCredits), "↻ --")]
    public void UnavailableDockItemsUseConsistentStatus(string kindName, string expectedTitle)
    {
        var kind = Enum.Parse<UsageDockItemKind>(kindName);
        var result = UsageDockItem.FormatUnavailable(kind);

        Assert.Equal(expectedTitle, result.Title);
        Assert.Equal("Codex usage unavailable", result.Subtitle);
    }

    [Fact]
    public void DetailFormatter_ReportsMissingExpiryRows()
    {
        var resets = new RateLimitResetCredits(
            2,
            [new RateLimitResetCredit("Full reset", "available", DateTimeOffset.FromUnixTimeSeconds(1784246400))]);

        var text = CodexUsageDockPage.FormatResetCredits(resets);

        Assert.Contains("Full reset", text, StringComparison.Ordinal);
        Assert.Contains("1 reset(s)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailFormatterEscapesExternalResetTitles()
    {
        var resets = new RateLimitResetCredits(
            1,
            [new RateLimitResetCredit("**Full reset**\n> spoof", "available", null)]);

        var text = CodexUsageDockPage.FormatResetCredits(resets);

        Assert.Contains(@"\*\*Full reset\*\* \> spoof", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n> spoof", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardSummary_WarnsWhenPrimaryLimitIsLow()
    {
        var now = new DateTimeOffset(2026, 7, 12, 14, 0, 0, TimeSpan.Zero);
        var snapshot = new CodexUsageSnapshot(
            new RateLimitWindow(92, 300, now.AddMinutes(36)),
            new RateLimitWindow(14, 10080, now.AddDays(2)),
            "pro",
            null,
            null,
            now,
            UsageDataSource.AppServer,
            null);

        var summary = CodexUsageDockPage.FormatSummary(snapshot, now);

        Assert.Contains("Almost at your limit", summary, StringComparison.Ordinal);
        Assert.Contains("8% available", summary, StringComparison.Ordinal);
        Assert.Contains("in 36 minutes", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100, "██████████")]
    [InlineData(47, "█████░░░░░")]
    [InlineData(0, "░░░░░░░░░░")]
    public void ProgressBar_RendersTenSegments(double remaining, string expected)
    {
        Assert.Equal(expected, CodexUsageDockPage.ProgressBar(remaining));
    }

    [Fact]
    public void Trend_EstimatesLimitTimeFromObservedConsumption()
    {
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        UsageHistoryEntry[] history =
        [
            new(now.AddMinutes(-30), 80),
            new(now, 60),
        ];

        var trend = CodexUsageDockPage.FormatTrend(history, now);

        Assert.Contains("80% → 60%", trend, StringComparison.Ordinal);
        Assert.Contains($"limit around {now.AddMinutes(90).ToLocalTime():HH:mm}", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void Trend_UsesOnlySamplesAfterLatestQuotaIncrease()
    {
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        UsageHistoryEntry[] history =
        [
            new(now.AddMinutes(-90), 10),
            new(now.AddMinutes(-60), 5),
            new(now.AddMinutes(-30), 100),
            new(now, 80),
        ];

        var trend = CodexUsageDockPage.FormatTrend(history, now);

        Assert.Contains("100% → 80%", trend, StringComparison.Ordinal);
        Assert.DoesNotContain("10%", trend, StringComparison.Ordinal);
        Assert.Contains($"limit around {now.AddHours(2).ToLocalTime():HH:mm}", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void Trend_SuppressesEstimateWhenLatestSampleIsStale()
    {
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        UsageHistoryEntry[] history =
        [
            new(now.AddMinutes(-90), 80),
            new(now.AddMinutes(-60), 60),
        ];

        var trend = CodexUsageDockPage.FormatTrend(history, now);

        Assert.Contains("too old for a reliable estimate", trend, StringComparison.Ordinal);
        Assert.DoesNotContain("limit around", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void TrendIsSuppressedWhileAllUsageDataIsUnavailable()
    {
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        UsageHistoryEntry[] history =
        [
            new(now.AddMinutes(-30), 80),
            new(now, 60),
        ];

        var trend = CodexUsageDockPage.FormatTrend(history, now, dataAvailable: false);

        Assert.Contains("Unavailable until a fresh usage sample", trend, StringComparison.Ordinal);
        Assert.DoesNotContain("limit around", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void DataStatus_IdentifiesStaleFallbackData()
    {
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        var snapshot = CodexUsageSnapshot.Loading with
        {
            UpdatedAt = now.AddMinutes(-18),
            Source = UsageDataSource.LocalSession,
        };

        var status = CodexUsageDockPage.FormatDataStatus(snapshot, now);

        Assert.Contains("Local fallback data", status, StringComparison.Ordinal);
        Assert.Contains("18 minutes", status, StringComparison.Ordinal);
        Assert.Equal("local Codex session metadata (desktop app, CLI, or another client)", snapshot.SourceDisplayName);
    }

    [Fact]
    public void SourceDisplayName_IdentifiesTheStandaloneCliAppServer()
    {
        var snapshot = CodexUsageSnapshot.Loading with { Source = UsageDataSource.AppServer };

        Assert.Equal("standalone Codex CLI app-server", snapshot.SourceDisplayName);
    }

    [Fact]
    public void DataStatus_IdentifiesLoadingWithoutClaimingFallbackData()
    {
        var now = DateTimeOffset.Now;

        var status = CodexUsageDockPage.FormatDataStatus(CodexUsageSnapshot.Loading, now);

        Assert.Contains("Loading data", status, StringComparison.Ordinal);
        Assert.DoesNotContain("Local fallback data", status, StringComparison.Ordinal);
    }

    [Fact]
    public void DataStatusIdentifiesCompletedUnavailableState()
    {
        var now = new DateTimeOffset(2026, 7, 14, 10, 30, 0, TimeSpan.Zero);
        var unavailable = CodexUsageSnapshot.Loading with
        {
            UpdatedAt = now,
            Source = UsageDataSource.Unavailable,
            Error = CodexUsageService.AllDataUnavailableMessage,
        };

        var status = CodexUsageDockPage.FormatDataStatus(unavailable, now);

        Assert.Contains("Data not available", status, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading data", status, StringComparison.Ordinal);
        Assert.Equal("not available", unavailable.SourceDisplayName);
    }

    [Fact]
    public async Task RefreshFallsBackWithoutExposingAppServerFailureDetails()
    {
        var fallback = CodexUsageSnapshot.Loading with
        {
            Secondary = new RateLimitWindow(12, 10080, DateTimeOffset.Now.AddDays(2)),
            UpdatedAt = DateTimeOffset.Now,
            Source = UsageDataSource.LocalSession,
            Error = null,
        };
        using var service = new CodexUsageService(
            _ => Task.FromException<CodexUsageSnapshot>(new InvalidOperationException(@"C:\Users\Alice\token.json")),
            () => fallback);

        await service.RefreshAsync();

        Assert.Equal(UsageDataSource.LocalSession, service.Current.Source);
        Assert.Equal(CodexUsageService.LiveDataUnavailableMessage, service.Current.Error);
        Assert.DoesNotContain("Alice", service.Current.Error, StringComparison.Ordinal);
        Assert.False(service.IsLoading);
    }

    [Fact]
    public async Task RefreshPublishesUnavailableStateWhenBothSourcesFail()
    {
        using var service = new CodexUsageService(
            _ => Task.FromException<CodexUsageSnapshot>(new InvalidOperationException("live failure")),
            () => throw new DirectoryNotFoundException(@"C:\Users\Alice\.codex\sessions"));

        await service.RefreshAsync();

        Assert.Null(service.Current.Primary);
        Assert.Null(service.Current.Secondary);
        Assert.Equal(UsageDataSource.Unavailable, service.Current.Source);
        Assert.Equal(CodexUsageService.AllDataUnavailableMessage, service.Current.Error);
        Assert.DoesNotContain("Alice", service.Current.Error, StringComparison.Ordinal);
        Assert.False(service.IsLoading);
    }

    [Fact]
    public async Task ConcurrentRefreshesShareTheSameInFlightTask()
    {
        var result = new TaskCompletionSource<CodexUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        using var service = new CodexUsageService(
            _ =>
            {
                Interlocked.Increment(ref readCount);
                return result.Task;
            },
            () => throw new InvalidOperationException("Fallback should not run."));

        var first = service.RefreshAsync();
        var second = service.RefreshAsync();

        Assert.Same(first, second);
        Assert.True(service.IsLoading);
        Assert.Equal(1, Volatile.Read(ref readCount));

        result.SetResult(CodexUsageSnapshot.Loading with
        {
            Secondary = new RateLimitWindow(10, 10080, DateTimeOffset.Now.AddDays(3)),
            Source = UsageDataSource.AppServer,
            Error = null,
        });
        await first;

        Assert.False(service.IsLoading);
        Assert.Equal(1, Volatile.Read(ref readCount));
    }

    [Fact]
    public async Task RefreshNotifiesLoadingAndCompletionWithoutTrustingSubscribers()
    {
        var loadingStates = new List<bool>();
        using var service = new CodexUsageService(
            _ => Task.FromResult(CodexUsageSnapshot.Loading with
            {
                Secondary = new RateLimitWindow(10, 10080, DateTimeOffset.Now.AddDays(3)),
                Source = UsageDataSource.AppServer,
                Error = null,
            }),
            () => throw new InvalidOperationException("Fallback should not run."));
        service.Updated += (_, _) => throw new InvalidOperationException("Subscriber failure");
        service.Updated += (_, _) => loadingStates.Add(service.IsLoading);

        await service.RefreshAsync();

        Assert.Equal([true, false], loadingStates);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightRefreshWithoutFaultingItsTask()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new CodexUsageService(
            async cancellationToken =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return CodexUsageSnapshot.Loading;
            },
            () => throw new InvalidOperationException("Fallback should not run."));

        var refresh = service.RefreshAsync();
        await started.Task.WaitAsync(AsyncTestTimeout);
        service.Dispose();

        await refresh.WaitAsync(AsyncTestTimeout);
        Assert.False(service.IsLoading);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightFallbackReader()
    {
        var fallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new CodexUsageService(
            _ => Task.FromException<CodexUsageSnapshot>(new InvalidOperationException("Live data unavailable.")),
            cancellationToken =>
            {
                fallbackStarted.SetResult();
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
                return CodexUsageSnapshot.Loading;
            });

        var refresh = service.RefreshAsync();
        await fallbackStarted.Task.WaitAsync(AsyncTestTimeout);
        service.Dispose();

        await refresh.WaitAsync(AsyncTestTimeout);
        Assert.False(service.IsLoading);
    }

    [Fact]
    public void LocalFallbackHonorsPreCanceledToken()
    {
        Assert.Throws<OperationCanceledException>(() => LocalCodexSessionReader.ReadLatest(new CancellationToken(canceled: true)));
    }

    [Fact]
    public void History_DeduplicatesAndPrunesStaleFallbackSamples()
    {
        using var service = new CodexUsageService();
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        var staleSnapshot = CodexUsageSnapshot.Loading with
        {
            Primary = new RateLimitWindow(40, 300, now.AddHours(1)),
            UpdatedAt = now.AddHours(-6),
            Source = UsageDataSource.LocalSession,
        };

        service.RecordHistory(staleSnapshot, now.AddHours(-5));
        service.RecordHistory(staleSnapshot, now.AddHours(-5).AddMinutes(1));
        Assert.Single(service.PrimaryHistory);

        service.RecordHistory(staleSnapshot, now);
        Assert.Empty(service.PrimaryHistory);
    }

    [Fact]
    public void History_RejectsFallbackSamplesOlderThanLatestLiveSample()
    {
        using var service = new CodexUsageService();
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        var liveSnapshot = CodexUsageSnapshot.Loading with
        {
            Primary = new RateLimitWindow(35, 300, now.AddHours(1)),
            UpdatedAt = now,
            Source = UsageDataSource.AppServer,
        };
        var olderFallback = liveSnapshot with
        {
            Primary = new RateLimitWindow(40, 300, now.AddHours(1)),
            UpdatedAt = now.AddMinutes(-30),
            Source = UsageDataSource.LocalSession,
        };

        service.RecordHistory(liveSnapshot, now);
        service.RecordHistory(olderFallback, now);

        var entry = Assert.Single(service.PrimaryHistory);
        Assert.Equal(now, entry.RecordedAt);
        Assert.Equal(65, entry.RemainingPercent);
    }

    [Fact]
    public void History_PrunesExpiredSamplesWhenPrimaryDataIsUnavailable()
    {
        using var service = new CodexUsageService();
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        var liveSnapshot = CodexUsageSnapshot.Loading with
        {
            Primary = new RateLimitWindow(35, 300, now.AddHours(-5)),
            UpdatedAt = now.AddHours(-6),
            Source = UsageDataSource.AppServer,
        };

        service.RecordHistory(liveSnapshot, now.AddHours(-5));
        Assert.Single(service.PrimaryHistory);

        service.RecordHistory(CodexUsageSnapshot.Loading, now);

        Assert.Empty(service.PrimaryHistory);
    }

    [Fact]
    public void WeeklyHistoryStorePersistsAndRestoresValidSamples()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(temporaryDirectory, "weekly-history.json");
        var now = DateTimeOffset.Now;
        try
        {
            var store = new WeeklyUsageHistoryStore(path);
            UsageHistoryEntry[] entries =
            [
                new(now.AddHours(-2), 80),
                new(now.AddMinutes(-1), 70),
            ];

            store.Save(entries);

            Assert.Equal(entries, store.Load(now).ToArray());
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void WeeklyHistoryStoreIgnoresCorruptAndExpiredEntries()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(temporaryDirectory, "weekly-history.json");
        var now = DateTimeOffset.Now;
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            File.WriteAllText(path, "not json");
            var store = new WeeklyUsageHistoryStore(path);

            Assert.Empty(store.Load(now));

            store.Save(
            [
                new UsageHistoryEntry(now.AddDays(-8), 80),
                new UsageHistoryEntry(now.AddMinutes(1), 60),
                new UsageHistoryEntry(now.AddMinutes(-1), 70),
            ]);

            var entry = Assert.Single(store.Load(now));
            Assert.Equal(70, entry.RemainingPercent);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void WeeklyHistoryPersistsAcrossServiceRestartsAndStaysSeparateFromFiveHourHistory()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(temporaryDirectory, "weekly-history.json");
        var now = DateTimeOffset.Now;
        var snapshot = CodexUsageSnapshot.Loading with
        {
            Primary = new RateLimitWindow(20, 300, now.AddHours(4)),
            Secondary = new RateLimitWindow(40, 10080, now.AddDays(6)),
            UpdatedAt = now,
            Source = UsageDataSource.AppServer,
        };
        try
        {
            using (var service = new CodexUsageService(_ => Task.FromResult(snapshot), () => snapshot, new WeeklyUsageHistoryStore(path)))
            {
                service.RecordHistory(snapshot, now);
                service.RecordHistory(snapshot, now);

                Assert.Single(service.PrimaryHistory);
                Assert.Single(service.WeeklyHistory);
            }

            using var restarted = new CodexUsageService(_ => Task.FromResult(snapshot), () => snapshot, new WeeklyUsageHistoryStore(path));
            Assert.Empty(restarted.PrimaryHistory);
            Assert.Single(restarted.WeeklyHistory);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void FiveHourTrendIsNotRenderedWhenFiveHourWindowIsInactive()
    {
        var now = DateTimeOffset.Now;
        var trend = CodexUsageDockPage.FormatTrend(
            "5-hour usage trend",
            [new UsageHistoryEntry(now.AddMinutes(-30), 80), new UsageHistoryEntry(now, 60)],
            window: null,
            now: now,
            dataAvailable: true,
            maximumSampleAge: TimeSpan.FromMinutes(5));

        Assert.Empty(trend);
    }

    [Fact]
    public void WeeklyTrendEstimatesRemainingAllowanceAtResetWhenLimitWillNotBeReached()
    {
        var now = DateTimeOffset.Now;
        var trend = CodexUsageDockPage.FormatTrend(
            "Weekly usage trend",
            [new UsageHistoryEntry(now.AddDays(-1), 80), new UsageHistoryEntry(now, 70)],
            new RateLimitWindow(30, 10080, now.AddDays(3)),
            now,
            dataAvailable: true,
            maximumSampleAge: TimeSpan.FromMinutes(5));

        Assert.Contains("Weekly usage trend", trend, StringComparison.Ordinal);
        Assert.Contains("40% available at reset", trend, StringComparison.Ordinal);
        Assert.DoesNotContain("limit around", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendEstimatesLimitWhenConsumptionWillExceedAllowanceBeforeReset()
    {
        var now = DateTimeOffset.Now;
        var trend = CodexUsageDockPage.FormatTrend(
            "Weekly usage trend",
            [new UsageHistoryEntry(now.AddHours(-1), 30), new UsageHistoryEntry(now, 10)],
            new RateLimitWindow(90, 10080, now.AddDays(3)),
            now,
            dataAvailable: true,
            maximumSampleAge: TimeSpan.FromMinutes(5));

        Assert.Contains("limit around", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendIncludesDateWhenTheEstimatedLimitIsNotToday()
    {
        var now = DateTimeOffset.Now;
        var estimated = now.AddHours(99);
        var trend = CodexUsageDockPage.FormatTrend(
            "Weekly usage trend",
            [new UsageHistoryEntry(now.AddHours(-1), 100), new UsageHistoryEntry(now, 99)],
            new RateLimitWindow(1, 10080, now.AddDays(6)),
            now,
            dataAvailable: true,
            maximumSampleAge: TimeSpan.FromMinutes(5));

        Assert.Contains($"limit around {estimated.ToLocalTime():ddd d MMM HH:mm}", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendUsesOnlySamplesAfterTheLatestWeeklyReset()
    {
        var now = DateTimeOffset.Now;
        var trend = CodexUsageDockPage.FormatTrend(
            "Weekly usage trend",
            [
                new UsageHistoryEntry(now.AddHours(-3), 10),
                new UsageHistoryEntry(now.AddHours(-2), 5),
                new UsageHistoryEntry(now.AddHours(-1), 100),
                new UsageHistoryEntry(now, 80),
            ],
            new RateLimitWindow(20, 10080, now.AddDays(6)),
            now,
            dataAvailable: true,
            maximumSampleAge: TimeSpan.FromMinutes(5));

        Assert.Contains("100% → 80%", trend, StringComparison.Ordinal);
        Assert.DoesNotContain("10%", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendFiltersSamplesBeforeCurrentWindowStartWhenNoUsageIncreaseOccurs()
    {
        var now = DateTimeOffset.Now;
        var reset = now.AddDays(6);
        var trend = CodexUsageDockPage.FormatTrend(
            "Weekly usage trend",
            [
                new UsageHistoryEntry(now.AddDays(-2), 90),
                new UsageHistoryEntry(now.AddDays(-1).AddMinutes(-1), 50),
                new UsageHistoryEntry(now.AddHours(-1), 40),
                new UsageHistoryEntry(now, 30),
            ],
            new RateLimitWindow(70, 10080, reset),
            now,
            dataAvailable: true,
            maximumSampleAge: TimeSpan.FromMinutes(5));

        Assert.Contains("40% → 30%", trend, StringComparison.Ordinal);
        Assert.DoesNotContain("50%", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void TrendAllowsFreshnessMatchingTheConfiguredRefreshInterval()
    {
        var now = DateTimeOffset.Now;
        var trend = CodexUsageDockPage.FormatTrend(
            "Weekly usage trend",
            [new UsageHistoryEntry(now.AddHours(-1), 80), new UsageHistoryEntry(now.AddMinutes(-10), 70)],
            new RateLimitWindow(30, 10080, now.AddDays(3)),
            now,
            dataAvailable: true,
            maximumSampleAge: TimeSpan.FromMinutes(15));

        Assert.DoesNotContain("too old for a reliable estimate", trend, StringComparison.Ordinal);
    }
}
