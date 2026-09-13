using Xunit;

namespace CodexUsageDock.Tests;

public sealed class CodexUsageTableTests
{
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
