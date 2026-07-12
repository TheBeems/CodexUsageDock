using System.Text.Json;
using Xunit;
namespace CodexUsageDock.Tests;

public sealed class UsageDataTests
{
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
        Assert.Contains("Voor 1 reset(s)", text, StringComparison.Ordinal);
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

        Assert.Contains("Bijna aan je limiet", summary, StringComparison.Ordinal);
        Assert.Contains("8% beschikbaar", summary, StringComparison.Ordinal);
        Assert.Contains("over 36 minuten", summary, StringComparison.Ordinal);
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
        Assert.Contains($"limiet rond {now.AddMinutes(90).ToLocalTime():HH:mm}", trend, StringComparison.Ordinal);
    }

    [Fact]
    public void DataStatus_IdentifiesStaleFallbackData()
    {
        var now = new DateTimeOffset(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);
        var snapshot = CodexUsageSnapshot.Loading with
        {
            UpdatedAt = now.AddMinutes(-18),
            Source = "lokale Codex-sessie",
        };

        var status = CodexUsageDockPage.FormatDataStatus(snapshot, now);

        Assert.Contains("Lokale reservegegevens", status, StringComparison.Ordinal);
        Assert.Contains("18 minuten", status, StringComparison.Ordinal);
    }

    [Fact]
    public void DataStatus_IdentifiesLoadingWithoutClaimingFallbackData()
    {
        var now = DateTimeOffset.Now;

        var status = CodexUsageDockPage.FormatDataStatus(CodexUsageSnapshot.Loading, now);

        Assert.Contains("Gegevens worden geladen", status, StringComparison.Ordinal);
        Assert.DoesNotContain("Lokale reservegegevens", status, StringComparison.Ordinal);
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
            Source = "lokale Codex-sessie",
        };

        service.RecordHistory(staleSnapshot, now.AddHours(-5));
        service.RecordHistory(staleSnapshot, now.AddHours(-5).AddMinutes(1));
        Assert.Single(service.PrimaryHistory);

        service.RecordHistory(staleSnapshot, now);
        Assert.Empty(service.PrimaryHistory);
    }
}
