using Xunit;

namespace CodexUsageDock.Tests;

public sealed class CodexUsageTableTests
{
    [Fact]
    public void RemappedWeeklyQuotaAppearsOnceAndOtherCategoriesStaySeparate()
    {
        var now = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        var weekly = new RateLimitWindow(49, 10080, now.AddDays(4));
        var snapshot = CodexUsageSnapshot.Loading with
        {
            Primary = null, Secondary = weekly, UpdatedAt = now, Source = UsageDataSource.AppServer,
            ResetCredits = new(0, null),
            DefaultBucketId = "codex",
            Buckets = [new("codex", "codex", weekly, null), new("spark", "Spark", weekly, null)],
        };
        var view = new UsagePresentation(snapshot, [], [], new([], null), LocalTokenUsageSnapshot.Unavailable, false);
        var body = CodexUsageTablePage.Format(view, now, TimeSpan.FromMinutes(1));
        var groups = UsageQuotaGroups.Create(snapshot);
        Assert.Equal(2, groups.Count);
        Assert.Single(groups[0].Windows, item => item.Window == weekly);
        Assert.Single(groups[1].Windows);
        Assert.Equal(2, body.Split("**Weekly:** 51% remaining", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, body.Split("Not reported", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("primary", body, StringComparison.Ordinal);
        Assert.DoesNotContain("10080", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FullTokenTotalsAndLocalDatesUseUnconstrainedTextRows()
    {
        var now = new DateTimeOffset(2026, 9, 15, 22, 1, 0, TimeSpan.Zero);
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("Test local", TimeSpan.FromHours(2), "Test local", "Test local");
        var snapshot = CodexUsageSnapshot.Loading with { UpdatedAt = now, Source = UsageDataSource.AppServer };
        var tokens = new LocalTokenUsageSnapshot([new(new DateOnly(2026, 9, 13), 51_732_933),
            new(new DateOnly(2026, 9, 15), 9_999_999_999)], now, LocalTokenUsageStatus.Complete);
        var view = new UsagePresentation(snapshot, [new(now, 51)], [new(now, 51)], new([], null), tokens, false);
        var body = CodexUsageTablePage.Format(view, now, TimeSpan.FromMinutes(1), timeZone);
        Assert.Contains("Observed: Wed 16 Sep 2026 00:01", body, StringComparison.Ordinal);
        Assert.Contains("Times are local (Test local)", body, StringComparison.Ordinal);
        Assert.Contains("- Sun 13 Sep 2026: **51,732,933** tokens", body, StringComparison.Ordinal);
        Assert.Contains("- Tue 15 Sep 2026: **9,999,999,999** tokens", body, StringComparison.Ordinal);
        Assert.DoesNotContain("| ---", body, StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TextViewKeepsUnknownZeroExpiredAndUnconfirmedStatesDistinct()
    {
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var quota = new CodexUsageSnapshot(new(100, 300, now.AddHours(-1)), null, null, null, new(0, null),
            now.AddHours(-2), UsageDataSource.LastConfirmed, null,
            [new("extra", "Extra | quota", new(0, 60, now.AddHours(1)), null)], DefaultBucketId: "codex");
        var view = new UsagePresentation(quota, [], [], new([], null), LocalTokenUsageSnapshot.Unavailable, false);
        var body = CodexUsageTablePage.Format(view, now, TimeSpan.FromMinutes(1));
        Assert.Contains("LastConfirmed", body, StringComparison.Ordinal);
        Assert.Contains("Reset passed; refresh required", body, StringComparison.Ordinal);
        Assert.Contains("Not reported", body, StringComparison.Ordinal);
        Assert.Contains("0%", body, StringComparison.Ordinal);
        Assert.Contains("100%", body, StringComparison.Ordinal);
        Assert.Contains("Extra \\| quota", body, StringComparison.Ordinal);
        Assert.DoesNotContain("![", body, StringComparison.Ordinal);
    }
}
