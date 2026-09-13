using Xunit;

namespace CodexUsageDock.Tests;

public sealed class UsageDiagnosticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResetCreditsDistinguishMissingFieldFromZeroAndDetailCount()
    {
        var missing = CodexUsageDiagnosticsPage.FormatDiagnostics(
            Snapshot(resetCredits: null),
            TimeSpan.FromMinutes(1),
            Now,
            version: "0.6.1");
        var zero = CodexUsageDiagnosticsPage.FormatDiagnostics(
            Snapshot(resetCredits: new RateLimitResetCredits(0, [])),
            TimeSpan.FromMinutes(1),
            Now,
            version: "0.6.1");
        var withDetails = CodexUsageDiagnosticsPage.FormatDiagnostics(
            Snapshot(resetCredits: new RateLimitResetCredits(3, [new RateLimitResetCredit("reset", "available", null)])),
            TimeSpan.FromMinutes(1),
            Now,
            version: "0.6.1");

        Assert.Contains("Reset credits:** unknown (field missing)", missing, StringComparison.Ordinal);
        Assert.Contains("Reset credit details:** unknown (field missing)", missing, StringComparison.Ordinal);
        Assert.Contains("Reset credits:** 0 available", zero, StringComparison.Ordinal);
        Assert.Contains("Reset credit details:** 0 provided", zero, StringComparison.Ordinal);
        Assert.Contains("Reset credits:** 3 available", withDetails, StringComparison.Ordinal);
        Assert.Contains("Reset credit details:** 1 provided", withDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void FutureMeasurementIsReportedAsClockSkewAndNotFresh()
    {
        var text = CodexUsageDiagnosticsPage.FormatDiagnostics(
            Snapshot(updatedAt: Now.AddMinutes(2)),
            TimeSpan.FromMinutes(1),
            Now,
            version: "0.6.1");

        Assert.Contains("Freshness:** future timestamp (clock skew suspected)", text, StringComparison.Ordinal);
        Assert.Contains("Measurement time (UTC):** 2026-09-09 12:02:00 UTC", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsDoNotExposeAccountKeysOrRawErrors()
    {
        const string accountKey = "account-secret-value";
        const string rawError = "C:\\Users\\Alice\\.codex\\auth.json bearer-token";
        var text = CodexUsageDiagnosticsPage.FormatDiagnostics(
            Snapshot(accountKey: accountKey, error: rawError),
            TimeSpan.FromMinutes(1),
            Now,
            historyStorageError: rawError,
            version: "0.6.1");

        Assert.DoesNotContain(accountKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(rawError, text, StringComparison.Ordinal);
        Assert.Contains("hashed identifier present; value withheld", text, StringComparison.Ordinal);
        Assert.Contains("error recorded (details withheld)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingBucketsAreDifferentFromAnAvailableEmptyBucketList()
    {
        var missing = CodexUsageDiagnosticsPage.FormatDiagnostics(
            Snapshot(buckets: null),
            TimeSpan.FromMinutes(1),
            Now,
            version: "0.6.1");
        var empty = CodexUsageDiagnosticsPage.FormatDiagnostics(
            Snapshot(buckets: []),
            TimeSpan.FromMinutes(1),
            Now,
            version: "0.6.1");

        Assert.Contains("Quota buckets:** unknown (field missing)", missing, StringComparison.Ordinal);
        Assert.Contains("Quota buckets:** available (0)", empty, StringComparison.Ordinal);
    }

    private static CodexUsageSnapshot Snapshot(
        DateTimeOffset? updatedAt = null,
        RateLimitResetCredits? resetCredits = null,
        IReadOnlyList<RateLimitBucket>? buckets = null,
        string? accountKey = null,
        string? error = null) => new(
            null,
            null,
            null,
            null,
            resetCredits,
            updatedAt ?? Now,
            UsageDataSource.AppServer,
            error,
            buckets,
            accountKey,
            null,
            Now,
            "codex");
}
