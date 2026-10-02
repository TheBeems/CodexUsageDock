using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class WeeklyResetIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset Reset = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetAndRestartPreserveObservedHistoryAndBootstrapFromLearnedUsage(bool earlyReset)
    {
        const string Account = "test-account";
        var now = Reset.AddMinutes(1);
        var oldReset = earlyReset ? Reset.AddDays(1) : Reset;
        var weeklyStore = new WeeklyUsageHistoryStore(_environment.PathFor("weekly.json"));
        var adaptiveStore = new AdaptiveWeeklyUsageStore(_environment.PathFor("adaptive.json"));
        var scopedAdaptive = adaptiveStore.ForContext($"{Account}|codex");
        for (var week = 3; week >= 1; week--)
        {
            var cycleReset = Reset.AddDays(-7 * week);
            var samples = WeeklyForecastTests.Series(cycleReset.AddDays(-7), cycleReset.AddMinutes(-5), 100, 80);
            scopedAdaptive.Record(new(20, 10080, cycleReset), samples, TimeSpan.FromMinutes(15));
        }

        var afterReset = Snapshot(Account, now, 100, Reset.AddDays(7));
        using (var service = _environment.CreateService(_ => Task.FromResult(afterReset), () => afterReset,
            weeklyStore, adaptiveStore, clock: () => now))
        {
            service.RecordHistory(Snapshot(Account, Reset.AddMinutes(-10), 20, oldReset), Reset.AddMinutes(-10));
            service.RecordHistory(Snapshot(Account, Reset.AddMinutes(-5), 18, oldReset), Reset.AddMinutes(-5));
            service.RecordHistory(afterReset, now);
            Assert.Equal(3, service.WeeklyHistory.Count);
        }

        using var restarted = _environment.CreateService(_ => Task.FromResult(afterReset), () => afterReset,
            weeklyStore, adaptiveStore, clock: () => now);
        restarted.RecordHistory(afterReset, now);
        Assert.Collection(restarted.WeeklyHistory,
            sample => Assert.Equal(20, sample.RemainingPercent),
            sample => Assert.Equal(18, sample.RemainingPercent),
            sample => Assert.Equal(100, sample.RemainingPercent));
        Assert.Equal(4, restarted.AdaptiveWeeklyHistory.CompletedCycles.Length);
        var partialCycle = Assert.Single(restarted.AdaptiveWeeklyHistory.CompletedCycles, cycle => cycle.ResetsAt == oldReset);
        Assert.Equal(5, partialCycle.ObservedMinutes);
        Assert.Equal(2, partialCycle.ConsumedPercent);

        using var dashboard = JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(afterReset, now, false,
            restarted.PrimaryHistory, restarted.WeeklyHistory, TimeSpan.FromMinutes(1), true,
            restarted.AdaptiveWeeklyHistory));
        var data = dashboard.RootElement;
        Assert.True(data.GetProperty("weeklyTrendAvailable").GetBoolean());
        Assert.Contains("historical usage only", data.GetProperty("weeklyForecastStatus").GetString(), StringComparison.Ordinal);
        Assert.Contains("3 usable weeks", data.GetProperty("weeklyForecastStatus").GetString(), StringComparison.Ordinal);
        Assert.Contains("0/30", data.GetProperty("weeklyForecastStatus").GetString(), StringComparison.Ordinal);
        var chart = XDocument.Parse(Uri.UnescapeDataString(
            data.GetProperty("weeklyTrendChartUrl").GetString()!["data:image/svg+xml;utf8,".Length..]));
        var observed = Assert.Single(chart.Descendants(Svg + "polyline"), line => line.Attribute("stroke-dasharray") is null);
        Assert.Equal(2, observed.Attribute("points")!.Value.Split(' ').Length);
        Assert.Single(chart.Descendants(Svg + "polyline"), line => line.Attribute("stroke-dasharray") is not null);
        Assert.Contains("amber: reset/restoration", data.GetProperty("chartLegend").GetString(), StringComparison.Ordinal);

        var otherAccount = afterReset with { AccountKey = "other-account" };
        restarted.RecordHistory(otherAccount, now);
        Assert.Single(restarted.WeeklyHistory);
        Assert.Empty(restarted.AdaptiveWeeklyHistory.CompletedCycles);
    }

    [Fact]
    public void DashboardShowsASingleFreshObservationAndHistoricalForecastAfterReset()
    {
        var now = Reset.AddMinutes(1);
        var snapshot = Snapshot("test-account", now, 100, Reset.AddDays(7));
        using var dashboard = JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(snapshot, now, false,
            [], [new(now, 100, snapshot.Secondary!.ResetsAt, 10080)], TimeSpan.FromMinutes(1), true,
            WeeklyForecastTests.History(_ => 0.002, snapshot.Secondary.ResetsAt)));

        Assert.True(dashboard.RootElement.GetProperty("weeklyTrendAvailable").GetBoolean());
        Assert.Contains("historical usage only", dashboard.RootElement.GetProperty("weeklyForecastStatus").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Forecast is unavailable", dashboard.RootElement.GetProperty("weeklyTrendChartAlt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenReadIncludesRetainedDaysBeforeTheCurrentQuotaWindow()
    {
        var now = Reset.AddMinutes(1);
        var snapshot = Snapshot("test-account", now, 100, Reset.AddDays(7));
        DateTimeOffset? readStart = null;
        DateTimeOffset? readEnd = null;
        var previousDate = DateOnly.FromDateTime(Reset.AddDays(-2).ToLocalTime().Date);
        using var service = _environment.CreateService(_ => Task.FromResult(snapshot), () => snapshot,
            localTokenUsageReader: (start, end, _, _) =>
            {
                readStart = start;
                readEnd = end;
                return Task.FromResult(new LocalTokenUsageSnapshot([new(previousDate, 1234)], now, LocalTokenUsageStatus.Complete));
            }, clock: () => now);
        service.RecordHistory(Snapshot("test-account", Reset.AddDays(-2), 20, Reset), Reset.AddDays(-2));
        await service.RefreshAsync();
        await service.TokenRefreshTask;

        Assert.Equal(now.AddDays(-7), readStart);
        Assert.Equal(now, readEnd);
        var presentation = service.GetPresentation();
        using var dashboard = JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(snapshot, now, false,
            presentation.PrimaryHistory, presentation.WeeklyHistory, TimeSpan.FromMinutes(1),
            tokenUsage: presentation.TokenUsage));
        var chart = XDocument.Parse(Uri.UnescapeDataString(
            dashboard.RootElement.GetProperty("weeklyTrendChartUrl").GetString()!["data:image/svg+xml;utf8,".Length..]));
        Assert.Single(chart.Descendants(Svg + "rect"), bar => bar.Attribute("data-series")?.Value == "daily-tokens"
            && bar.Attribute("data-date")?.Value == previousDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            && bar.Attribute("data-tokens")?.Value == "1234");
    }

    private static CodexUsageSnapshot Snapshot(string account, DateTimeOffset at, double remaining, DateTimeOffset reset) =>
        CodexUsageSnapshot.Loading with
        {
            Source = UsageDataSource.AppServer,
            Secondary = new(100 - remaining, 10080, reset),
            UpdatedAt = at,
            AccountKey = account,
            DefaultBucketId = "codex",
            Error = null,
        };
}
