using System.Text.Json;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class UsageFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 5)]
    [InlineData(5, 5)]
    [InlineData(15, 15)]
    public void SharedPolicyUsesAtLeastFiveMinutes(int refreshMinutes, int expectedMinutes)
    {
        var refreshInterval = TimeSpan.FromMinutes(refreshMinutes);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), UsageFreshness.MaximumAge(refreshInterval));
        Assert.Equal(UsageFreshness.MaximumAge(refreshInterval), UsageTrendHistory.Freshness(refreshInterval));
    }

    [Fact]
    public void FreshnessClassifiesUnknownFutureAndLastConfirmedSafely()
    {
        Assert.Equal(
            UsageFreshnessState.Unknown,
            UsageFreshness.Classify(null, Now, TimeSpan.FromMinutes(1)));
        Assert.Equal(
            UsageFreshnessState.Future,
            UsageFreshness.Classify(Now.AddMinutes(1), Now, TimeSpan.FromMinutes(1)));
        Assert.Equal(
            UsageFreshnessState.LastConfirmed,
            UsageFreshness.Classify(Now, Now, TimeSpan.FromMinutes(1), isLastConfirmed: true));
    }

    [Fact]
    public void DataStatusUsesTheSharedFiveMinuteMinimum()
    {
        var snapshot = Snapshot(updatedAt: Now.AddMinutes(-4));
        var status = CodexUsageDockPage.FormatDataStatus(snapshot, Now, TimeSpan.FromMinutes(1));

        Assert.Contains("just updated", status, StringComparison.Ordinal);
        Assert.DoesNotContain("possibly outdated", status, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleSummaryDoesNotClaimPlentyOfAllowance()
    {
        var snapshot = Snapshot(updatedAt: Now.AddMinutes(-6));
        var summary = CodexUsageDockPage.FormatSummary(snapshot, Now, TimeSpan.FromMinutes(1));

        Assert.Contains("Usage data is stale", summary, StringComparison.Ordinal);
        Assert.Contains("Last known allowance", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Plenty of allowance", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void LastConfirmedSuppressesForecastButPreservesObservedChart()
    {
        var reset = Now.AddDays(6);
        var snapshot = Snapshot(updatedAt: Now.AddHours(-1)) with
        {
            Primary = null,
            Secondary = new RateLimitWindow(20, 10080, reset),
            Source = UsageDataSource.LastConfirmed,
        };
        var history = new[]
        {
            new UsageHistoryEntry(Now.AddMinutes(-10), 90),
            new UsageHistoryEntry(Now, 80),
        };

        using var document = JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(
            snapshot,
            Now,
            isLoading: false,
            primaryHistory: [],
            weeklyHistory: history,
            refreshInterval: TimeSpan.FromMinutes(1)));
        var root = document.RootElement;

        Assert.Contains("Last confirmed usage", root.GetProperty("statusTitle").GetString(), StringComparison.Ordinal);
        Assert.Contains("Projection unavailable", root.GetProperty("weeklyProjection").GetString(), StringComparison.Ordinal);
        Assert.Equal("Forecast unavailable until usage data is refreshed.", root.GetProperty("weeklyForecastStatus").GetString());
        Assert.True(root.GetProperty("weeklyTrendAvailable").GetBoolean());
        Assert.StartsWith("data:image/svg+xml;utf8,", root.GetProperty("weeklyTrendChartUrl").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredWindowIsReportedAsUnknownInsteadOfZero()
    {
        var snapshot = Snapshot(updatedAt: Now) with
        {
            Primary = new RateLimitWindow(100, 300, Now.AddMinutes(-1)),
            Secondary = null,
        };

        var summary = CodexUsageDockPage.FormatSummary(snapshot, Now, TimeSpan.FromMinutes(1));
        using var document = JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(
            snapshot,
            Now,
            isLoading: false,
            primaryHistory: [],
            weeklyHistory: [],
            refreshInterval: TimeSpan.FromMinutes(1)));

        Assert.Contains("allowance unknown", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("0%", summary, StringComparison.Ordinal);
        Assert.False(document.RootElement.GetProperty("fiveHourAvailable").GetBoolean());
    }

    [Fact]
    public void AdditionalBucketsUseSeparateTruthfulDurationRows()
    {
        var snapshot = Snapshot(updatedAt: Now) with
        {
            Buckets =
            [
                new RateLimitBucket(
                    "default",
                    "Default",
                    new RateLimitWindow(10, 60, Now.AddHours(1)),
                    new RateLimitWindow(20, 10080, Now.AddDays(6))),
                new RateLimitBucket(
                    "coding",
                    "Coding <quota>",
                    new RateLimitWindow(25, 15, Now.AddMinutes(10)),
                    new RateLimitWindow(50, 90, Now.AddHours(1))),
                new RateLimitBucket(
                    "review",
                    "Review",
                    new RateLimitWindow(25, 15, Now.AddMinutes(10)),
                    new RateLimitWindow(50, 90, Now.AddHours(1))),
            ],
            DefaultBucketId = "default",
        };

        var details = CodexUsageDockPage.FormatAdditionalBucketDetails(snapshot, Now);

        Assert.Contains("Other quota windows", details, StringComparison.Ordinal);
        Assert.Contains("Coding \\<quota\\>", details, StringComparison.Ordinal);
        Assert.Contains("15-minute", details, StringComparison.Ordinal);
        Assert.Contains("90-minute", details, StringComparison.Ordinal);
        Assert.Contains("Default", details, StringComparison.Ordinal);
        Assert.Contains("1-hour", details, StringComparison.Ordinal);
        Assert.Contains("75% available", details, StringComparison.Ordinal);
        Assert.Contains("50% available", details, StringComparison.Ordinal);
        Assert.Equal(2, details.Split("75% available", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void DockFreshnessUsesTheConfiguredInterval()
    {
        var snapshot = Snapshot(updatedAt: Now.AddMinutes(-6));

        var status = UsageDockItem.FormatSourceFreshness(snapshot, Now, TimeSpan.FromMinutes(1));

        Assert.Contains("Stale", status, StringComparison.Ordinal);
        Assert.Contains("6 minutes", status, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshBlockedAccountHasAnExplicitOverallStatus()
    {
        var snapshot = Snapshot(updatedAt: Now) with { OrdinaryUsageAllowed = false };

        var summary = CodexUsageDockPage.FormatSummary(snapshot, Now, TimeSpan.FromMinutes(1));

        Assert.Contains("Ordinary usage blocked", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Plenty of allowance", summary, StringComparison.Ordinal);
    }

    private static CodexUsageSnapshot Snapshot(DateTimeOffset updatedAt) => new(
        new RateLimitWindow(20, 300, Now.AddHours(4)),
        new RateLimitWindow(30, 10080, Now.AddDays(6)),
        "pro",
        null,
        null,
        updatedAt,
        UsageDataSource.AppServer,
        null);
}
