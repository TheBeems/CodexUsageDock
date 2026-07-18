using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;
namespace CodexUsageDock.Tests;

public sealed class UsageDataTests
{
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(5);
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static XDocument ParseSvg(string imageUrl)
    {
        const string Prefix = "data:image/svg+xml;utf8,";
        Assert.StartsWith(Prefix, imageUrl, StringComparison.Ordinal);
        return XDocument.Parse(Uri.UnescapeDataString(imageUrl[Prefix.Length..]));
    }

    [Fact]
    public void SettingsDefaultToShowingAllDockUsageInformation()
    {
        var settings = new CodexUsageDockSettingsPage();

        Assert.True(settings.ShowFiveHourLimit);
        Assert.True(settings.ShowWeeklyLimit);
        Assert.True(settings.ShowResetsAndCredits);
        Assert.True(settings.ShowResetTime);
        Assert.True(settings.UseAdaptiveWeeklyForecast);
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

        Assert.Equal("0.5.3", CodexUsageDockMetadata.Version);
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

    [Fact]
    public void DetailsPageUsesNativeMediumDetailsPane()
    {
        using var service = new CodexUsageService();
        using var page = new CodexUsageDockPage(service, new CodexUsageDockSettingsPage());

        var details = Assert.IsType<Details>(page.Details);
        var main = Assert.IsType<FormContent>(Assert.Single(page.GetContent()));
        using var template = JsonDocument.Parse(main.TemplateJson);

        Assert.Equal("Usage details", details.Title);
        Assert.Equal(ContentSize.Medium, details.Size);
        Assert.Equal("AdaptiveCard", template.RootElement.GetProperty("type").GetString());
        Assert.Contains("\"type\": \"Image\"", main.TemplateJson, StringComparison.Ordinal);
        Assert.Contains("Allowance used", main.TemplateJson, StringComparison.Ordinal);
        Assert.Contains("Window elapsed", main.TemplateJson, StringComparison.Ordinal);
        Assert.Contains("weeklyTrendAvailable", main.TemplateJson, StringComparison.Ordinal);
        Assert.Contains("Solid: sampled allowance", main.TemplateJson, StringComparison.Ordinal);
        Assert.Same(Assert.Single(page.GetContent()), Assert.Single(page.GetContent()));
    }

    [Fact]
    public void DetailsPageSeparatesQuotaContentFromSecondaryDetails()
    {
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new CodexUsageSnapshot(
            new RateLimitWindow(20, 300, now.AddHours(4)),
            new RateLimitWindow(2, 10080, now.AddDays(6)),
            "pro_lite",
            new CreditBalance(true, false, "10.00"),
            new RateLimitResetCredits(
                3,
                [new RateLimitResetCredit("Full reset", "available", now.AddDays(13))]),
            now,
            UsageDataSource.AppServer,
            null);
        var main = CodexUsageDockPage.FormatMainDataJson(
            snapshot,
            now,
            isLoading: false,
            [new UsageHistoryEntry(now.AddMinutes(-30), 90), new UsageHistoryEntry(now, 80)],
            [
                new UsageHistoryEntry(now.AddHours(-12), 99),
                new UsageHistoryEntry(now.AddMinutes(-10), 98.01),
                new UsageHistoryEntry(now, 98),
            ],
            TimeSpan.FromMinutes(1));
        var details = CodexUsageDockPage.FormatDetailsBody(snapshot, now);
        using var mainData = JsonDocument.Parse(main);
        var root = mainData.RootElement;

        Assert.Equal("Status: Plenty of allowance available", root.GetProperty("statusTitle").GetString());
        Assert.Equal("80%", root.GetProperty("fiveHourRemaining").GetString());
        Assert.Equal("98%", root.GetProperty("weeklyRemaining").GetString());
        Assert.Equal("20%", root.GetProperty("fiveHourUsedPercent").GetString());
        Assert.Equal("20%", root.GetProperty("fiveHourElapsedPercent").GetString());
        Assert.Equal("2%", root.GetProperty("weeklyUsedPercent").GetString());
        Assert.Equal("14%", root.GetProperty("weeklyElapsedPercent").GetString());
        Assert.StartsWith("data:image/svg+xml;utf8,", root.GetProperty("fiveHourUsedBarUrl").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("data:image/svg+xml;utf8,", root.GetProperty("fiveHourElapsedBarUrl").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("data:image/svg+xml;utf8,", root.GetProperty("weeklyUsedBarUrl").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("data:image/svg+xml;utf8,", root.GetProperty("weeklyElapsedBarUrl").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("weeklyTrendAvailable").GetBoolean());
        Assert.StartsWith("data:image/svg+xml;utf8,", root.GetProperty("weeklyTrendChartUrl").GetString(), StringComparison.Ordinal);
        Assert.Contains("Solid line connects sampled values", root.GetProperty("weeklyTrendChartAlt").GetString(), StringComparison.Ordinal);
        Assert.Equal("Forecast: current pace only.", root.GetProperty("weeklyForecastStatus").GetString());
        Assert.Equal("On track", root.GetProperty("fiveHourPaceStatus").GetString());
        Assert.Equal("Comfortably on track", root.GetProperty("weeklyPaceStatus").GetString());
        Assert.Contains("Projected at reset", root.GetProperty("fiveHourProjection").GetString(), StringComparison.Ordinal);
        Assert.Contains("Projected at reset", root.GetProperty("weeklyProjection").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Resets and credits", main, StringComparison.Ordinal);
        Assert.DoesNotContain("Account and data", main, StringComparison.Ordinal);
        Assert.DoesNotContain("standalone Codex CLI app-server", main, StringComparison.Ordinal);
        Assert.DoesNotContain("Codex Usage -", main, StringComparison.Ordinal);

        Assert.Contains("Resets and credits", details, StringComparison.Ordinal);
        Assert.Contains("3 available", details, StringComparison.Ordinal);
        Assert.Contains("Full reset", details, StringComparison.Ordinal);
        Assert.Contains("10.00", details, StringComparison.Ordinal);
        Assert.Contains("Pro Lite", details, StringComparison.Ordinal);
        Assert.Contains("standalone Codex CLI app-server", details, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendUsesFullWindowHistoryBeforeTheLatestQuotaIncrease()
    {
        var windowStart = new DateTimeOffset(2026, 7, 16, 11, 59, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = new DateTimeOffset(2026, 7, 17, 10, 36, 0, TimeSpan.Zero);
        var snapshot = new CodexUsageSnapshot(
            null,
            new RateLimitWindow(20, 10080, reset),
            null,
            null,
            null,
            now,
            UsageDataSource.AppServer,
            null);
        var data = CodexUsageDockPage.FormatMainDataJson(
            snapshot,
            now,
            isLoading: false,
            primaryHistory: [],
            weeklyHistory:
            [
                new UsageHistoryEntry(windowStart.AddMinutes(1), 100),
                new UsageHistoryEntry(windowStart.AddMinutes(5), 85),
                new UsageHistoryEntry(windowStart.AddMinutes(6), 96),
                new UsageHistoryEntry(windowStart.AddMinutes(10), 90),
                new UsageHistoryEntry(new DateTimeOffset(2026, 7, 17, 10, 31, 0, TimeSpan.Zero), 88),
                new UsageHistoryEntry(now, 80),
            ],
            refreshInterval: TimeSpan.FromMinutes(1));

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var chart = ParseSvg(root.GetProperty("weeklyTrendChartUrl").GetString()!);
        var chartRoot = Assert.IsType<XElement>(chart.Root);
        var thursdayUseBar = Assert.Single(
            chartRoot.Descendants(Svg + "rect"),
            rect => rect.Attribute("data-series")?.Value == "daily-use"
                && rect.Attribute("data-date")?.Value == "2026-07-16");
        var observedLines = chartRoot.Descendants(Svg + "polyline")
            .Where(line => line.Attribute("stroke-dasharray") is null)
            .ToArray();

        Assert.True(root.GetProperty("weeklyTrendAvailable").GetBoolean());
        Assert.InRange(
            double.Parse(thursdayUseBar.Attribute("height")!.Value, CultureInfo.InvariantCulture),
            22.2,
            22.3);
        Assert.Equal(2, observedLines.Length);
    }

    [Fact]
    public void WeeklyDashboardUsesAdaptiveLocalHistory()
    {
        var reset = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        var start = reset.AddDays(-7);
        var now = start.AddHours(6);
        var snapshot = new CodexUsageSnapshot(
            null,
            new RateLimitWindow(20, 10080, reset),
            null,
            null,
            null,
            now,
            UsageDataSource.AppServer,
            null);
        var cycles = Enumerable.Range(1, 3)
            .Select(offset => new AdaptiveWeeklyUsageCycle(
                reset.AddDays(-7 * offset),
                10080,
                60,
                6,
                [new AdaptiveWeeklyUsageBucket(1, 60, 12)]))
            .ToArray();

        var data = CodexUsageDockPage.FormatMainDataJson(
            snapshot,
            now,
            isLoading: false,
            primaryHistory: [],
            weeklyHistory:
            [
                new UsageHistoryEntry(now.AddHours(-1), 90),
                new UsageHistoryEntry(now, 80),
            ],
            refreshInterval: TimeSpan.FromMinutes(1),
            adaptiveWeeklyForecastEnabled: true,
            adaptiveWeeklyHistory: new AdaptiveWeeklyUsageHistory(cycles, null));

        using var document = JsonDocument.Parse(data);

        Assert.Equal(
            "Forecast: current pace + local history (3/8 cycles).",
            document.RootElement.GetProperty("weeklyForecastStatus").GetString());
    }

    [Fact]
    public async Task DetailsPageRefreshUpdatesMainContentAndDetailsPane()
    {
        var now = DateTimeOffset.Now;
        var result = new TaskCompletionSource<CodexUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new CodexUsageService(
            _ => result.Task,
            () => throw new InvalidOperationException("Fallback should not run."));
        using var page = new CodexUsageDockPage(service, new CodexUsageDockSettingsPage());
        var main = Assert.IsType<FormContent>(Assert.Single(page.GetContent()));
        var details = Assert.IsType<Details>(page.Details);

        var refresh = service.RefreshAsync();

        Assert.True(page.IsLoading);
        using (var loadingData = JsonDocument.Parse(main.DataJson))
        {
            Assert.True(loadingData.RootElement.GetProperty("isLoading").GetBoolean());
        }

        result.SetResult(CodexUsageSnapshot.Loading with
        {
            Primary = new RateLimitWindow(25, 300, now.AddHours(4)),
            PlanType = "pro",
            ResetCredits = new RateLimitResetCredits(2, null),
            UpdatedAt = now,
            Source = UsageDataSource.AppServer,
            Error = null,
        });
        await refresh.WaitAsync(AsyncTestTimeout);

        Assert.False(page.IsLoading);
        using (var refreshedData = JsonDocument.Parse(main.DataJson))
        {
            Assert.False(refreshedData.RootElement.GetProperty("isLoading").GetBoolean());
            Assert.Equal("75%", refreshedData.RootElement.GetProperty("fiveHourRemaining").GetString());
            Assert.StartsWith(
                "data:image/svg+xml;utf8,",
                refreshedData.RootElement.GetProperty("fiveHourUsedBarUrl").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(
                "Projection will appear after another measurement.",
                refreshedData.RootElement.GetProperty("fiveHourProjection").GetString());
        }

        Assert.Contains("2 available", details.Body, StringComparison.Ordinal);
        Assert.Contains("Pro", details.Body, StringComparison.Ordinal);
        Assert.Contains("standalone Codex CLI app-server", details.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageProgressBarCreatesSvgDataUri()
    {
        var bar = UsageDashboardCard.CreateProgressBarImageUrl(75, UsageBarPalette.FiveHour);

        Assert.StartsWith("data:image/svg+xml;utf8,", bar, StringComparison.Ordinal);
        Assert.Contains("%3Csvg", bar, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%2339B8E3", bar, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsageProgressBarHandlesNonFinitePercentage()
    {
        var bar = UsageDashboardCard.CreateProgressBarImageUrl(double.NaN, UsageBarPalette.Weekly);

        Assert.StartsWith("data:image/svg+xml;utf8,", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", bar, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendChartRendersObservedForecastAndDailyUse()
    {
        var windowStart = new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = windowStart.AddDays(2).AddHours(6);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(windowStart.AddMinutes(10), 100),
                new UsageHistoryEntry(windowStart.AddHours(4), 95),
                new UsageHistoryEntry(windowStart.AddDays(1).AddHours(4), 88),
                new UsageHistoryEntry(now, 80),
            ],
            new RateLimitWindow(20, 10080, reset),
            now,
            TimeSpan.FromDays(2),
            new UsageTrendForecast(reset, 60, false));

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var svg = ParseSvg(result.ImageUrl);
        var root = svg.Root;
        Assert.NotNull(root);
        var lines = root!.Descendants(Svg + "polyline").ToArray();

        Assert.Equal($"0 0 {UsageDashboardCard.BarWidth} {WeeklyUsageTrendChartRenderer.Height}", root.Attribute("viewBox")?.Value);
        Assert.Contains(lines, line => line.Attribute("stroke-dasharray") is null);
        Assert.Contains(lines, line => line.Attribute("stroke-dasharray")?.Value == "5 4");
        Assert.True(root.Descendants(Svg + "rect").Count(rect => rect.Attribute("data-series")?.Value == "daily-use") >= 2);
        Assert.Contains("Solid line connects sampled values", result.AltText, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", result.ImageUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", result.ImageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendChartConnectsGapsWithoutAddingUnobservedDailyUse()
    {
        var windowStart = new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = windowStart.AddDays(1).AddHours(1);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(windowStart.AddMinutes(1), 100),
                new UsageHistoryEntry(windowStart.AddMinutes(5), 90),
                new UsageHistoryEntry(windowStart.AddDays(1).AddMinutes(1), 60),
                new UsageHistoryEntry(windowStart.AddDays(1).AddMinutes(5), 50),
            ],
            new RateLimitWindow(50, 10080, reset),
            now,
            TimeSpan.FromMinutes(15),
            new UsageTrendForecast(reset, 0, false));

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var svg = ParseSvg(result.ImageUrl);
        var root = svg.Root;
        Assert.NotNull(root);
        var observedLines = root!.Descendants(Svg + "polyline")
            .Where(line => line.Attribute("stroke-dasharray") is null)
            .ToArray();
        var observedBarHeight = root.Descendants(Svg + "rect")
            .Where(bar => bar.Attribute("data-series")?.Value == "daily-use")
            .Sum(bar => double.Parse(bar.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture));

        var observedPoints = Assert.Single(observedLines)
            .Attribute("points")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, observedPoints.Length);
        Assert.InRange(observedBarHeight, 21.1, 21.3);
    }

    [Fact]
    public void WeeklyTrendChartBreaksAtQuotaIncreaseAndRetainsCalendarDayUse()
    {
        var windowStart = new DateTimeOffset(2026, 7, 16, 11, 59, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = windowStart.AddMinutes(10);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(windowStart.AddMinutes(1), 100),
                new UsageHistoryEntry(windowStart.AddMinutes(5), 85),
                new UsageHistoryEntry(windowStart.AddMinutes(6), 96),
                new UsageHistoryEntry(now, 86),
            ],
            new RateLimitWindow(14, 10080, reset),
            now,
            TimeSpan.FromMinutes(15),
            forecast: null,
            timeZone: TimeZoneInfo.Utc);

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var root = Assert.IsType<XElement>(ParseSvg(result.ImageUrl).Root);
        var observedLines = root.Descendants(Svg + "polyline")
            .Where(line => line.Attribute("stroke-dasharray") is null)
            .ToArray();
        var dailyUseBar = Assert.Single(
            root.Descendants(Svg + "rect"),
            rect => rect.Attribute("data-series")?.Value == "daily-use"
                && rect.Attribute("data-date")?.Value == "2026-07-16");

        Assert.Equal(2, observedLines.Length);
        Assert.InRange(
            double.Parse(dailyUseBar.Attribute("height")!.Value, CultureInfo.InvariantCulture),
            26.4,
            26.6);
        Assert.Contains("line breaks mark allowance increases or resets", result.AltText, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendChartOmitsForecastWhenLatestObservationIsGapIsolated()
    {
        var windowStart = new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = windowStart.AddDays(1);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(windowStart.AddMinutes(1), 100),
                new UsageHistoryEntry(windowStart.AddMinutes(5), 95),
                new UsageHistoryEntry(now, 80),
            ],
            new RateLimitWindow(20, 10080, reset),
            now,
            TimeSpan.FromMinutes(15),
            new UsageTrendForecast(reset, 0, false));

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var svg = ParseSvg(result.ImageUrl);

        Assert.Single(svg.Descendants(Svg + "polyline"), line => line.Attribute("stroke-dasharray") is null);
        Assert.DoesNotContain(svg.Descendants(Svg + "polyline"), line => line.Attribute("stroke-dasharray") is not null);
        Assert.Contains("Forecast is unavailable", result.AltText, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendChartRendersHostSafeLocalizedAxisLabels()
    {
        var culture = CultureInfo.GetCultureInfo("nl-NL");
        var windowStart = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = windowStart.AddDays(1);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(windowStart.AddMinutes(1), 100),
                new UsageHistoryEntry(now, 90),
            ],
            new RateLimitWindow(10, 10080, reset),
            now,
            TimeSpan.FromDays(2),
            forecast: null,
            culture: culture,
            timeZone: TimeZoneInfo.Utc);

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var svg = ParseSvg(result.ImageUrl);
        var root = Assert.IsType<XElement>(svg.Root);
        var verticalLabels = root.Descendants(Svg + "g")
            .Where(group => group.Attribute("data-axis")?.Value == "vertical")
            .Select(group => group.Attribute("data-axis-label")!.Value)
            .ToArray();
        var horizontalLabels = root.Descendants(Svg + "g")
            .Where(group => group.Attribute("data-axis")?.Value == "horizontal")
            .Select(group => group.Attribute("data-axis-label")!.Value)
            .ToArray();

        Assert.Equal(["100%", "50%", "0%"], verticalLabels);
        Assert.Equal(["ma 13", "di 14", "wo 15", "do 16", "vr 17", "za 18", "zo 19", "ma 20"], horizontalLabels);
        Assert.Equal(["MA 13", "DI 14", "WO 15", "DO 16", "VR 17", "ZA 18", "ZO 19", "MA 20"], root.Descendants(Svg + "g")
            .Where(group => group.Attribute("data-axis")?.Value == "horizontal")
            .Select(group => group.Attribute("data-rendered-label")!.Value));
        Assert.Empty(root.Descendants(Svg + "text"));
        Assert.All(root.Descendants(Svg + "g").Where(group => group.Attribute("data-axis") is not null), group =>
            Assert.NotEmpty(group.Descendants(Svg + "rect")));
        Assert.Contains("vertical scale is percentages", result.AltText, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendChartFallsBackToInvariantWeekdaysForUnsupportedGlyphs()
    {
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        var windowStart = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = windowStart.AddDays(1);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(windowStart.AddMinutes(1), 100),
                new UsageHistoryEntry(now, 90),
            ],
            new RateLimitWindow(10, 10080, reset),
            now,
            TimeSpan.FromDays(2),
            forecast: null,
            culture: culture,
            timeZone: TimeZoneInfo.Utc);

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var root = Assert.IsType<XElement>(ParseSvg(result.ImageUrl).Root);
        var horizontalLabels = root.Descendants(Svg + "g")
            .Where(group => group.Attribute("data-axis")?.Value == "horizontal")
            .ToArray();

        Assert.Equal(["Mon 13", "Tue 14", "Wed 15", "Thu 16", "Fri 17", "Sat 18", "Sun 19", "Mon 20"],
            horizontalLabels.Select(group => group.Attribute("data-axis-label")!.Value));
        Assert.All(horizontalLabels, group =>
            Assert.DoesNotContain('?', group.Attribute("data-rendered-label")!.Value));
    }

    [Fact]
    public void WeeklyTrendChartAttributesUseToLocalCalendarDays()
    {
        var culture = CultureInfo.GetCultureInfo("nl-NL");
        var windowStart = new DateTimeOffset(2026, 7, 16, 11, 59, 37, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = new DateTimeOffset(2026, 7, 17, 16, 25, 13, TimeSpan.Zero);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(new DateTimeOffset(2026, 7, 17, 10, 31, 38, TimeSpan.Zero), 88),
                new UsageHistoryEntry(new DateTimeOffset(2026, 7, 17, 11, 59, 32, TimeSpan.Zero), 73),
                new UsageHistoryEntry(new DateTimeOffset(2026, 7, 17, 16, 24, 17, TimeSpan.Zero), 64),
            ],
            new RateLimitWindow(36, 10080, reset),
            now,
            TimeSpan.FromDays(1),
            forecast: null,
            culture: culture,
            timeZone: TimeZoneInfo.Utc);

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var root = Assert.IsType<XElement>(ParseSvg(result.ImageUrl).Root);
        var dailyUseBar = Assert.Single(
            root.Descendants(Svg + "rect"),
            rect => rect.Attribute("data-series")?.Value == "daily-use");
        var horizontalLabels = root.Descendants(Svg + "g")
            .Where(group => group.Attribute("data-axis")?.Value == "horizontal")
            .Select(group => group.Attribute("data-axis-label")!.Value)
            .ToArray();

        Assert.Equal("2026-07-17", dailyUseBar.Attribute("data-date")?.Value);
        Assert.Equal("false", dailyUseBar.Attribute("data-partial")?.Value);
        Assert.Equal("true", dailyUseBar.Attribute("data-current")?.Value);
        Assert.Equal(["do 16", "vr 17", "za 18", "zo 19", "ma 20", "di 21", "wo 22", "do 23"], horizontalLabels);
        Assert.Equal(7, root.Descendants(Svg + "line").Count(line => line.Attribute("data-grid")?.Value == "calendar-day"));
        Assert.Equal(2, root.Descendants(Svg + "line").Count(line => line.Attribute("data-marker")?.Value?.StartsWith("reset-", StringComparison.Ordinal) == true));
        Assert.Single(root.Descendants(Svg + "line"), line => line.Attribute("data-marker")?.Value == "now");
        Assert.Contains("horizontal labels are local calendar dates", result.AltText, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendChartUsesEqualWidthCalendarDayColumns()
    {
        var windowStart = new DateTimeOffset(2026, 7, 16, 11, 59, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = new DateTimeOffset(2026, 7, 17, 0, 5, 0, TimeSpan.Zero);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(windowStart.AddMinutes(1), 100),
                new UsageHistoryEntry(windowStart.AddMinutes(5), 90),
                new UsageHistoryEntry(new DateTimeOffset(2026, 7, 17, 0, 1, 0, TimeSpan.Zero), 85),
                new UsageHistoryEntry(now, 75),
            ],
            new RateLimitWindow(25, 10080, reset),
            now,
            TimeSpan.FromMinutes(15),
            forecast: null,
            culture: CultureInfo.GetCultureInfo("nl-NL"),
            timeZone: TimeZoneInfo.Utc);

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var root = Assert.IsType<XElement>(ParseSvg(result.ImageUrl).Root);
        var dailyUseBars = root.Descendants(Svg + "rect")
            .Where(rect => rect.Attribute("data-series")?.Value == "daily-use")
            .OrderBy(rect => double.Parse(rect.Attribute("x")!.Value, CultureInfo.InvariantCulture))
            .ToArray();
        var firstX = double.Parse(dailyUseBars[0].Attribute("x")!.Value, CultureInfo.InvariantCulture);
        var secondX = double.Parse(dailyUseBars[1].Attribute("x")!.Value, CultureInfo.InvariantCulture);
        var firstWidth = double.Parse(dailyUseBars[0].Attribute("width")!.Value, CultureInfo.InvariantCulture);
        var secondWidth = double.Parse(dailyUseBars[1].Attribute("width")!.Value, CultureInfo.InvariantCulture);
        var labels = root.Descendants(Svg + "g")
            .Where(group => group.Attribute("data-axis")?.Value == "horizontal")
            .Select(group => group.Attribute("data-axis-label")!.Value)
            .ToArray();

        Assert.Equal("true", dailyUseBars[0].Attribute("data-partial")?.Value);
        Assert.Equal("false", dailyUseBars[1].Attribute("data-partial")?.Value);
        Assert.Equal(firstWidth, secondWidth, precision: 3);
        Assert.Equal(firstWidth + 6, secondX - firstX, precision: 3);
        Assert.Equal(["do 16", "vr 17", "za 18", "zo 19", "ma 20", "di 21", "wo 22", "do 23"], labels);
    }

    [Fact]
    public void WeeklyTrendChartUsesTheZeroGridlineAsTheSharedBarBaseline()
    {
        var windowStart = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = windowStart.AddHours(2);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(windowStart.AddMinutes(1), 100),
                new UsageHistoryEntry(windowStart.AddHours(1), 80),
                new UsageHistoryEntry(now, 70),
            ],
            new RateLimitWindow(10, 10080, reset),
            now,
            TimeSpan.FromDays(2),
            forecast: null);

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var root = Assert.IsType<XElement>(ParseSvg(result.ImageUrl).Root);
        var zeroGridline = Assert.Single(
            root.Descendants(Svg + "line"),
            line => line.Attribute("data-grid")?.Value == "remaining-percent"
                && line.Attribute("data-value")?.Value == "0");
        var zeroY = double.Parse(zeroGridline.Attribute("y1")!.Value, CultureInfo.InvariantCulture);
        var bottomGridY = root.Descendants(Svg + "line")
            .Where(line => line.Attribute("data-grid")?.Value == "remaining-percent")
            .Max(line => double.Parse(line.Attribute("y1")!.Value, CultureInfo.InvariantCulture));
        var dailyUseBar = Assert.Single(
            root.Descendants(Svg + "rect"),
            rect => rect.Attribute("data-series")?.Value == "daily-use");
        var barBottom = double.Parse(dailyUseBar.Attribute("y")!.Value, CultureInfo.InvariantCulture)
            + double.Parse(dailyUseBar.Attribute("height")!.Value, CultureInfo.InvariantCulture);
        var zeroLabel = Assert.Single(
            root.Descendants(Svg + "g"),
            group => group.Attribute("data-axis")?.Value == "vertical"
                && group.Attribute("data-axis-label")?.Value == "0%");
        var labelTop = zeroLabel.Descendants(Svg + "rect")
            .Min(rect => double.Parse(rect.Attribute("y")!.Value, CultureInfo.InvariantCulture));

        Assert.Equal(bottomGridY, zeroY, precision: 3);
        Assert.Equal(zeroY, barBottom, precision: 3);
        Assert.Equal(zeroY - 5, labelTop, precision: 3);
    }

    [Fact]
    public void WeeklyTrendChartConnectsGapIsolatedObservationsAsASampledLine()
    {
        var windowStart = new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = windowStart.AddDays(1);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(windowStart.AddMinutes(1), 100),
                new UsageHistoryEntry(now, 80),
            ],
            new RateLimitWindow(20, 10080, reset),
            now,
            TimeSpan.FromMinutes(15),
            forecast: null);

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var svg = ParseSvg(result.ImageUrl);

        var observed = Assert.Single(svg.Descendants(Svg + "polyline"));
        var points = observed.Attribute("points")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, points.Length);
        Assert.Single(svg.Descendants(Svg + "circle"));
    }

    [Fact]
    public void WeeklyTrendChartOmitsForecastWithoutAProjection()
    {
        var windowStart = new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = windowStart.AddHours(1);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(windowStart.AddMinutes(1), 100),
                new UsageHistoryEntry(now, 95),
            ],
            new RateLimitWindow(5, 10080, reset),
            now,
            TimeSpan.FromMinutes(15),
            forecast: null);

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var svg = ParseSvg(result.ImageUrl);

        Assert.DoesNotContain(svg.Descendants(Svg + "polyline"), line => line.Attribute("stroke-dasharray") is not null);
        Assert.Contains("Forecast is unavailable", result.AltText, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyTrendChartBoundsMinuteHistory()
    {
        var windowStart = new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);
        var reset = windowStart.AddDays(7);
        var now = reset.AddMinutes(-1);
        var samples = Enumerable.Range(0, 10080)
            .Select(index => new UsageHistoryEntry(
                windowStart.AddMinutes(index),
                100 - index * 50d / 10079d))
            .ToArray();
        var chart = WeeklyUsageTrendChartRenderer.Create(
            samples,
            new RateLimitWindow(50, 10080, reset),
            now,
            TimeSpan.FromMinutes(15),
            forecast: null,
            culture: CultureInfo.GetCultureInfo("en-US"),
            timeZone: TimeZoneInfo.Utc);

        var result = Assert.IsType<WeeklyUsageTrendChart>(chart);
        var svg = ParseSvg(result.ImageUrl);
        var observed = Assert.Single(svg.Descendants(Svg + "polyline"));
        var points = observed.Attribute("points")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.InRange(points.Length, 2, WeeklyUsageTrendChartRenderer.MaximumRenderedPoints);
        Assert.True(result.ImageUrl.Length < 80000, result.ImageUrl.Length.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void UsagePaceWarnsWhenAllowanceRunsFarAheadOfElapsedTime()
    {
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var snapshot = CodexUsageSnapshot.Loading with
        {
            Primary = new RateLimitWindow(70, 300, now.AddHours(4)),
            UpdatedAt = now,
            Source = UsageDataSource.AppServer,
        };

        var data = CodexUsageDockPage.FormatMainDataJson(
            snapshot,
            now,
            isLoading: false,
            primaryHistory: [],
            weeklyHistory: [],
            refreshInterval: TimeSpan.FromMinutes(1));
        using var document = JsonDocument.Parse(data);

        Assert.Equal("70%", document.RootElement.GetProperty("fiveHourUsedPercent").GetString());
        Assert.Equal("20%", document.RootElement.GetProperty("fiveHourElapsedPercent").GetString());
        Assert.Equal("Limit may be reached before reset", document.RootElement.GetProperty("fiveHourPaceStatus").GetString());
        Assert.Equal("Attention", document.RootElement.GetProperty("fiveHourPaceColor").GetString());
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
    [InlineData(null, null, "-- resets")]
    [InlineData(0, null, "0 resets")]
    [InlineData(2, "10.00", "2 resets · 10.00")]
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
    [InlineData(nameof(UsageDockItemKind.ResetsAndCredits), "-- resets")]
    public void UnavailableDockItemsUseConsistentStatus(string kindName, string expectedTitle)
    {
        var kind = Enum.Parse<UsageDockItemKind>(kindName);
        var result = UsageDockItem.FormatUnavailable(kind);

        Assert.Equal(expectedTitle, result.Title);
        Assert.Equal("Codex usage unavailable", result.Subtitle);
    }

    [Fact]
    public void ResetExpiryUsesTheNextFutureExpiryRoundedUpToWholeDays()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var resets = new RateLimitResetCredits(
            2,
            [
                new RateLimitResetCredit("Later reset", "available", now.AddDays(15)),
                new RateLimitResetCredit("Next reset", "available", now.AddDays(12).AddHours(1)),
                new RateLimitResetCredit("Expired reset", "available", now.AddDays(-1)),
            ]);

        Assert.Equal("expires in 13 days", UsageDockItem.FormatResetExpiry(resets, now));
    }

    [Fact]
    public void ResetExpiryReportsUnavailableWhenNoFutureExpiryIsKnown()
    {
        Assert.Equal("expiration unavailable", UsageDockItem.FormatResetExpiry(null, DateTimeOffset.Now));
    }

    [Fact]
    public void ResetExpiryUsesWholeHoursWhenLessThanOneDayRemains()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var resets = new RateLimitResetCredits(
            1,
            [new RateLimitResetCredit("Next reset", "available", now.AddHours(12).AddMinutes(1))]);

        Assert.Equal("expires in 13 hours", UsageDockItem.FormatResetExpiry(resets, now));
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
        Assert.Contains($"limit may be reached around {now.AddMinutes(90).ToLocalTime():HH:mm}", trend, StringComparison.Ordinal);
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
        Assert.Contains($"limit may be reached around {now.AddHours(2).ToLocalTime():HH:mm}", trend, StringComparison.Ordinal);
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

        Assert.Contains("latest measurement is too old", trend, StringComparison.Ordinal);
        Assert.DoesNotContain("limit may be reached", trend, StringComparison.Ordinal);
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

        Assert.Contains("Projection unavailable until fresh usage data", trend, StringComparison.Ordinal);
        Assert.DoesNotContain("limit may be reached", trend, StringComparison.Ordinal);
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
        Assert.Contains("Projected at reset: 40% available", trend, StringComparison.Ordinal);
        Assert.DoesNotContain("limit may be reached", trend, StringComparison.Ordinal);
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

        Assert.Contains("limit may be reached", trend, StringComparison.Ordinal);
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

        Assert.Contains($"limit may be reached around {estimated.ToLocalTime():ddd d MMM HH:mm}", trend, StringComparison.Ordinal);
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

        Assert.DoesNotContain("latest measurement is too old", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveWeeklyUsageStoreLearnsOnlyContinuousAllowanceDecreasesAndSealsAtReset()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(temporaryDirectory, "adaptive-weekly.json");
        var reset = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        var start = reset.AddDays(-7);
        var window = new RateLimitWindow(0, 10080, reset);
        try
        {
            var store = new AdaptiveWeeklyUsageStore(path);
            store.Record(
                window,
                [
                    new UsageHistoryEntry(start.AddMinutes(1), 100),
                    new UsageHistoryEntry(start.AddMinutes(2), 90),
                    new UsageHistoryEntry(start.AddMinutes(3), 95),
                    new UsageHistoryEntry(start.AddMinutes(4), 85),
                    new UsageHistoryEntry(start.AddHours(8), 80),
                ],
                TimeSpan.FromMinutes(5));

            var active = Assert.IsType<AdaptiveWeeklyUsageCycle>(store.Snapshot.ActiveCycle);
            Assert.Equal(2, active.ObservedMinutes, precision: 6);
            Assert.Equal(20, active.ConsumedPercent, precision: 6);
            var bucket = Assert.Single(active.Buckets);
            Assert.Equal(0, bucket.Index);
            Assert.Equal(20, bucket.ConsumedPercent, precision: 6);

            var restored = new AdaptiveWeeklyUsageStore(path);
            Assert.Equal(20, Assert.IsType<AdaptiveWeeklyUsageCycle>(restored.Snapshot.ActiveCycle).ConsumedPercent, precision: 6);

            var nextReset = reset.AddDays(7);
            store.Record(
                new RateLimitWindow(0, 10080, nextReset),
                [new UsageHistoryEntry(nextReset.AddDays(-7).AddMinutes(1), 100)],
                TimeSpan.FromMinutes(5));

            Assert.Single(store.Snapshot.CompletedCycles);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ReenablingAdaptiveForecastDoesNotLearnMeasurementsCollectedWhilePaused()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var historyPath = Path.Combine(temporaryDirectory, "weekly-history.json");
        var adaptivePath = Path.Combine(temporaryDirectory, "adaptive-weekly.json");
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddDays(6);

        CodexUsageSnapshot CreateSnapshot(double remainingPercent, DateTimeOffset updatedAt) => new(
            null,
            new RateLimitWindow(100 - remainingPercent, 10080, reset),
            null,
            null,
            null,
            updatedAt,
            UsageDataSource.AppServer,
            null);

        var latest = CreateSnapshot(70, now.AddMinutes(-1));
        try
        {
            using var service = new CodexUsageService(
                _ => Task.FromResult(latest),
                () => latest,
                new WeeklyUsageHistoryStore(historyPath),
                new AdaptiveWeeklyUsageStore(adaptivePath));

            service.RecordHistory(CreateSnapshot(100, now.AddMinutes(-4)), now);
            service.RecordHistory(CreateSnapshot(90, now.AddMinutes(-3)), now);
            Assert.Equal(10, Assert.IsType<AdaptiveWeeklyUsageCycle>(service.AdaptiveWeeklyHistory.ActiveCycle).ConsumedPercent, precision: 6);

            service.SetAdaptiveWeeklyForecastEnabled(false);
            service.RecordHistory(CreateSnapshot(80, now.AddMinutes(-2)), now);
            service.RecordHistory(latest, now);
            await service.RefreshAsync();

            service.SetAdaptiveWeeklyForecastEnabled(true);

            Assert.Equal(10, Assert.IsType<AdaptiveWeeklyUsageCycle>(service.AdaptiveWeeklyHistory.ActiveCycle).ConsumedPercent, precision: 6);

            service.RecordHistory(CreateSnapshot(60, now), now);
            service.RecordHistory(CreateSnapshot(50, now.AddMinutes(1)), now.AddMinutes(1));

            Assert.Equal(20, Assert.IsType<AdaptiveWeeklyUsageCycle>(service.AdaptiveWeeklyHistory.ActiveCycle).ConsumedPercent, precision: 6);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void ClearingAdaptiveWeeklyHistoryDoesNotRelearnOlderRawSamples()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(temporaryDirectory, "adaptive-weekly.json");
        var reset = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        var start = reset.AddDays(-7);
        var samples = new[]
        {
            new UsageHistoryEntry(start.AddMinutes(1), 100),
            new UsageHistoryEntry(start.AddMinutes(2), 90),
            new UsageHistoryEntry(start.AddMinutes(3), 80),
        };
        try
        {
            var store = new AdaptiveWeeklyUsageStore(path);
            var window = new RateLimitWindow(0, 10080, reset);
            store.Record(window, samples, TimeSpan.FromMinutes(5));
            Assert.True(Assert.IsType<AdaptiveWeeklyUsageCycle>(store.Snapshot.ActiveCycle).ConsumedPercent > 0);

            store.Clear();
            store.Record(window, samples, TimeSpan.FromMinutes(5));

            var active = Assert.IsType<AdaptiveWeeklyUsageCycle>(store.Snapshot.ActiveCycle);
            Assert.Equal(0, active.ObservedMinutes, precision: 6);
            Assert.Equal(0, active.ConsumedPercent, precision: 6);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void AdaptiveWeeklyUsageStoreRetainsOnlyEightCompletedCycles()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(temporaryDirectory, "adaptive-weekly.json");
        var firstReset = new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero);
        try
        {
            var store = new AdaptiveWeeklyUsageStore(path);
            for (var index = 0; index < 10; index++)
            {
                var reset = firstReset.AddDays(7 * index);
                var start = reset.AddDays(-7);
                store.Record(
                    new RateLimitWindow(0, 10080, reset),
                    [
                        new UsageHistoryEntry(start.AddMinutes(1), 100),
                        new UsageHistoryEntry(start.AddMinutes(2), 99),
                    ],
                    TimeSpan.FromMinutes(5));
            }

            var completed = store.Snapshot.CompletedCycles;
            Assert.Equal(8, completed.Length);
            Assert.Equal(firstReset.AddDays(7), completed[0].ResetsAt);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void AdaptiveForecastUsesSixHourPatternAfterThreeCompletedCycles()
    {
        var reset = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        var start = reset.AddDays(-7);
        var cycles = Enumerable.Range(1, 3)
            .Select(offset => new AdaptiveWeeklyUsageCycle(
                reset.AddDays(-7 * offset),
                10080,
                60,
                6,
                [new AdaptiveWeeklyUsageBucket(1, 60, 12)]))
            .ToArray();
        var history = new AdaptiveWeeklyUsageHistory(cycles, null);
        var latest = new UsageHistoryEntry(start.AddHours(6), 50);

        var adaptive = AdaptiveWeeklyForecast.Project(latest, start, reset, 0.1, true, history);
        var currentOnly = AdaptiveWeeklyForecast.Project(latest, start, reset, 0.1, false, history);

        Assert.Equal("Forecast: current pace + local history (3/8 cycles).", adaptive.Status);
        Assert.True(adaptive.Forecast.EndsAt < currentOnly.Forecast.EndsAt);
        Assert.Equal("Forecast: current pace only.", currentOnly.Status);
    }

    [Fact]
    public void WeeklyChartRendersEveryAdaptiveForecastPoint()
    {
        var reset = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        var start = reset.AddDays(-7);
        var now = start.AddDays(2);
        var chart = WeeklyUsageTrendChartRenderer.Create(
            [
                new UsageHistoryEntry(start.AddMinutes(1), 100),
                new UsageHistoryEntry(now.AddMinutes(-1), 81),
                new UsageHistoryEntry(now, 80),
            ],
            new RateLimitWindow(20, 10080, reset),
            now,
            TimeSpan.FromMinutes(5),
            new UsageTrendForecast(
                reset,
                20,
                false,
                [
                    new UsageTrendForecastPoint(now.AddHours(6), 75),
                    new UsageTrendForecastPoint(now.AddHours(12), 68),
                    new UsageTrendForecastPoint(reset, 20),
                ]));

        var document = ParseSvg(Assert.IsType<WeeklyUsageTrendChart>(chart).ImageUrl);
        var dashed = Assert.Single(
            document.Descendants(Svg + "polyline"),
            element => (string?)element.Attribute("stroke-dasharray") == "5 4");
        Assert.True(((string?)dashed.Attribute("points"))!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4);
    }
}
