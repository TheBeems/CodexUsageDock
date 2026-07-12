using System.Text.Json;
using Xunit;
namespace CodexUsageDock.Tests;

public sealed class UsageDataTests
{
    [Fact]
    public void ClassifyWindows_RecognizesWeeklyWindowWhenItIsPrimary()
    {
        var weekly = new RateLimitWindow(3, 10080, DateTimeOffset.Now.AddDays(7));

        var result = CodexUsageService.ClassifyWindows(weekly, null);

        Assert.Null(result.FiveHour);
        Assert.Same(weekly, result.Weekly);
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
        var workingDirectory = CodexUsageService.GetSafeWorkingDirectory("codex.exe");

        Assert.True(Path.IsPathRooted(workingDirectory));
        Assert.False(string.Equals(Environment.SystemDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase));
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

        var resets = CodexUsageService.ParseResetCredits(json.RootElement);

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

        var resets = CodexUsageService.ParseResetCredits(json.RootElement);

        Assert.Equal(3, resets!.AvailableCount);
        Assert.Null(resets.Credits);
    }

    [Fact]
    public void ParseCredits_HandlesBalanceAndUnlimitedAccounts()
    {
        using var result = JsonDocument.Parse("{}");
        using var balanceLimits = JsonDocument.Parse("""{ "credits": { "hasCredits": true, "unlimited": false, "balance": "10.00" } }""");
        using var unlimitedLimits = JsonDocument.Parse("""{ "credits": { "hasCredits": true, "unlimited": true, "balance": null } }""");

        var balance = CodexUsageService.ParseCredits(result.RootElement, balanceLimits.RootElement);
        var unlimited = CodexUsageService.ParseCredits(result.RootElement, unlimitedLimits.RootElement);

        Assert.Equal("10.00", balance!.Balance);
        Assert.True(unlimited!.Unlimited);
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
            "test",
            null);

        Assert.Equal(expected, UsageDockItem.FormatResetsAndCredits(snapshot));
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
            "Codex app-server",
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
    public void DataStatus_IdentifiesStaleFallbackData()
    {
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        var snapshot = CodexUsageSnapshot.Loading with
        {
            UpdatedAt = now.AddMinutes(-18),
            Source = "local Codex session",
        };

        var status = CodexUsageDockPage.FormatDataStatus(snapshot, now);

        Assert.Contains("Local fallback data", status, StringComparison.Ordinal);
        Assert.Contains("18 minutes", status, StringComparison.Ordinal);
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
    public void History_DeduplicatesAndPrunesStaleFallbackSamples()
    {
        using var service = new CodexUsageService();
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        var staleSnapshot = CodexUsageSnapshot.Loading with
        {
            Primary = new RateLimitWindow(40, 300, now.AddHours(1)),
            UpdatedAt = now.AddHours(-6),
            Source = "local Codex session",
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
            Source = "Codex app-server",
        };
        var olderFallback = liveSnapshot with
        {
            Primary = new RateLimitWindow(40, 300, now.AddHours(1)),
            UpdatedAt = now.AddMinutes(-30),
            Source = "local Codex session",
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
            Source = "Codex app-server",
        };

        service.RecordHistory(liveSnapshot, now.AddHours(-5));
        Assert.Single(service.PrimaryHistory);

        service.RecordHistory(CodexUsageSnapshot.Loading, now);

        Assert.Empty(service.PrimaryHistory);
    }
}
