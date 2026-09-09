using Xunit;

namespace CodexUsageDock.Tests;

public sealed class UsageAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly UsageAlertOptions EnabledOptions = new(true);

    [Fact]
    public void EnablingUsesTheFirstFreshMeasurementAsBaselineThenAlertsOnCrossing()
    {
        var evaluator = new UsageAlertEvaluator();

        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50)));

        var crossing = Evaluate(evaluator, Presentation(primaryRemaining: 9));
        var alert = Assert.Single(crossing);
        Assert.StartsWith("low:", alert.Key, StringComparison.Ordinal);
        Assert.Contains("5-hour window", alert.Message, StringComparison.Ordinal);
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 9)));
    }

    [Fact]
    public void LowAlertRearmsOnlyAfterARecoveryAboveThresholdPlusFive()
    {
        var evaluator = new UsageAlertEvaluator();

        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50)));
        Assert.Single(Evaluate(evaluator, Presentation(primaryRemaining: 9)));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 12)));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 15)));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 16)));
        Assert.Single(Evaluate(evaluator, Presentation(primaryRemaining: 9)));
    }

    [Fact]
    public void ResetTimestampJitterWithinOneMinuteStaysInTheSameCycle()
    {
        var evaluator = new UsageAlertEvaluator();
        var reset = Now.AddHours(4);

        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50, primaryReset: reset)));
        Assert.Single(Evaluate(evaluator, Presentation(
            primaryRemaining: 9,
            primaryReset: reset.AddSeconds(5))));
        Assert.Empty(Evaluate(evaluator, Presentation(
            primaryRemaining: 9,
            primaryReset: reset.AddSeconds(10))));
    }

    [Fact]
    public void NewCycleAndAccountSwitchStartCleanBaselines()
    {
        var evaluator = new UsageAlertEvaluator();
        var firstReset = Now.AddHours(4);
        var nextReset = firstReset.AddMinutes(2);

        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50, primaryReset: firstReset)));
        Assert.Single(Evaluate(evaluator, Presentation(primaryRemaining: 9, primaryReset: firstReset)));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 9, primaryReset: nextReset)));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50, primaryReset: nextReset)));
        Assert.Single(Evaluate(evaluator, Presentation(primaryRemaining: 9, primaryReset: nextReset)));

        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50, accountKey: "account-a")));
        Assert.Single(Evaluate(evaluator, Presentation(primaryRemaining: 9, accountKey: "account-a")));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 9, accountKey: "account-b")));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50, accountKey: "account-b")));
        Assert.Single(Evaluate(evaluator, Presentation(primaryRemaining: 9, accountKey: "account-b")));
    }

    [Fact]
    public void CategorySwitchStartsACleanBaseline()
    {
        var evaluator = new UsageAlertEvaluator();

        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50, defaultBucketId: "codex")));
        Assert.Single(Evaluate(evaluator, Presentation(primaryRemaining: 9, defaultBucketId: "codex")));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 9, defaultBucketId: "review")));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50, defaultBucketId: "review")));
        Assert.Single(Evaluate(evaluator, Presentation(primaryRemaining: 9, defaultBucketId: "review")));
    }

    [Fact]
    public void IneligibleSnapshotsNeverProduceAlerts()
    {
        var cases = new[]
        {
            Presentation(primaryRemaining: 9, updatedAt: Now.AddMinutes(-6)),
            Presentation(primaryRemaining: 9, updatedAt: Now.AddMinutes(1)),
            Presentation(primaryRemaining: 9, source: UsageDataSource.LastConfirmed),
            Presentation(primaryRemaining: 9, source: UsageDataSource.Unavailable),
            Presentation(primaryRemaining: 9, isLoading: true),
            Presentation(primaryRemaining: 9, accountKey: null),
        };

        foreach (var presentation in cases)
        {
            Assert.Empty(Evaluate(new UsageAlertEvaluator(), presentation));
        }
    }

    [Fact]
    public void ExpiredOrInvalidWindowsAreIgnored()
    {
        var expired = Presentation(primaryRemaining: 9, primaryReset: Now.AddMinutes(-1));
        var invalid = Presentation(primaryRemaining: 9) with
        {
            Usage = Presentation(primaryRemaining: null).Usage with
            {
                Primary = new RateLimitWindow(double.NaN, 300, Now.AddHours(4)),
            },
        };

        Assert.Empty(Evaluate(new UsageAlertEvaluator(), expired));
        Assert.Empty(Evaluate(new UsageAlertEvaluator(), invalid));
    }

    [Fact]
    public void AdditionalCategoriesStaySeparateAndLabelsAreSanitized()
    {
        var longName = new string('x', 100) + "\nraw account label";
        var highBuckets = new[]
        {
            new RateLimitBucket("coding", longName, new RateLimitWindow(50, 60, Now.AddHours(1)), null),
            new RateLimitBucket("review", "Review", new RateLimitWindow(50, 60, Now.AddHours(1)), null),
        };
        var lowBuckets = new[]
        {
            new RateLimitBucket("coding", longName, new RateLimitWindow(95, 60, Now.AddHours(1)), null),
            new RateLimitBucket("review", "Review", new RateLimitWindow(95, 60, Now.AddHours(1)), null),
        };
        var evaluator = new UsageAlertEvaluator();

        Assert.Empty(Evaluate(evaluator, Presentation(
            primaryRemaining: null,
            secondaryRemaining: null,
            buckets: highBuckets)));
        var alerts = Evaluate(evaluator, Presentation(
            primaryRemaining: null,
            secondaryRemaining: null,
            buckets: lowBuckets));

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, alert => alert.Message.Contains("Review", StringComparison.Ordinal));
        Assert.Contains(alerts, alert => alert.Message.Contains(new string('x', 70), StringComparison.Ordinal));
        Assert.DoesNotContain(new string('x', 71), alerts[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("raw account label", alerts[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("account-a", string.Join(" ", alerts.Select(alert => alert.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultBucketExtraDurationIsTrackedSeparately()
    {
        var evaluator = new UsageAlertEvaluator();
        var high = new[]
        {
            new RateLimitBucket(
                "codex",
                "Default",
                new RateLimitWindow(50, 60, Now.AddHours(1)),
                null),
        };
        var low = new[]
        {
            new RateLimitBucket(
                "codex",
                "Default",
                new RateLimitWindow(95, 60, Now.AddHours(1)),
                null),
        };

        Assert.Empty(Evaluate(evaluator, Presentation(
            primaryRemaining: 50,
            secondaryRemaining: null,
            buckets: high,
            defaultBucketId: "codex")));
        var alerts = Evaluate(evaluator, Presentation(
            primaryRemaining: 50,
            secondaryRemaining: null,
            buckets: low,
            defaultBucketId: "codex"));

        var alert = Assert.Single(alerts);
        Assert.Contains("1-hour window", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowTrackingIsBounded()
    {
        var highBuckets = Enumerable.Range(0, 40)
            .Select(index => new RateLimitBucket(
                $"bucket-{index}",
                $"Bucket {index}",
                new RateLimitWindow(50, 60, Now.AddHours(1)),
                null))
            .ToArray();
        var lowBuckets = highBuckets
            .Select(bucket => bucket with { Primary = bucket.Primary! with { UsedPercent = 95 } })
            .ToArray();
        var evaluator = new UsageAlertEvaluator();

        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: null, secondaryRemaining: null, buckets: highBuckets)));
        var alerts = Evaluate(evaluator, Presentation(primaryRemaining: null, secondaryRemaining: null, buckets: lowBuckets));

        Assert.Equal(32, alerts.Count);
    }

    [Fact]
    public void DisablingOrChangingOptionsClearsTheBaseline()
    {
        var evaluator = new UsageAlertEvaluator();
        var disabled = new UsageAlertOptions(false);
        var changed = new UsageAlertOptions(true, LowRemainingPercent: 20);

        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50), options: EnabledOptions));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 9), options: disabled));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 9), options: EnabledOptions));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 50), options: EnabledOptions));
        Assert.Single(Evaluate(evaluator, Presentation(primaryRemaining: 9), options: EnabledOptions));

        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 19), options: changed));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 10), options: changed));
        Assert.Empty(Evaluate(evaluator, Presentation(primaryRemaining: 30), options: changed));
        Assert.Single(Evaluate(evaluator, Presentation(primaryRemaining: 19), options: changed));
    }

    [Fact]
    public void ResetExpiryWarnsOnEntryWithinTwentyFourHoursAndDeduplicatesByExpiry()
    {
        var evaluator = new UsageAlertEvaluator();
        var expiry = Now.AddHours(25);
        var outside = new RateLimitResetCredits(1, [new RateLimitResetCredit("First title", null, expiry)]);
        var inside = new RateLimitResetCredits(1, [new RateLimitResetCredit("Second title", "available", expiry)]);

        Assert.Empty(Evaluate(evaluator, Presentation(
            primaryRemaining: null,
            secondaryRemaining: null,
            resetCredits: outside), now: Now));
        var transition = Evaluate(evaluator, Presentation(
            primaryRemaining: null,
            secondaryRemaining: null,
            resetCredits: inside,
            updatedAt: Now), now: Now.AddHours(2));
        Assert.Single(transition);
        Assert.StartsWith("reset-expiry:", transition[0].Key, StringComparison.Ordinal);
        Assert.DoesNotContain("First title", transition[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Second title", transition[0].Message, StringComparison.Ordinal);

        Assert.Empty(Evaluate(evaluator, Presentation(
            primaryRemaining: null,
            secondaryRemaining: null,
            resetCredits: new RateLimitResetCredits(1, [new RateLimitResetCredit("Changed title", "available", expiry)]),
            updatedAt: Now), now: Now.AddHours(2)));
        Assert.Empty(Evaluate(evaluator, Presentation(
            primaryRemaining: null,
            secondaryRemaining: null,
            resetCredits: new RateLimitResetCredits(1, [new RateLimitResetCredit("Used", "used", expiry)]),
            updatedAt: Now), now: Now.AddHours(2)));
    }

    [Fact]
    public void ForecastWarningUsesMatchingFreshHistoryAndBaselinesPredictiveRisk()
    {
        var evaluator = new UsageAlertEvaluator();
        var reset = Now.AddHours(4);
        UsageHistoryEntry[] firstHistory =
        [
            new(Now, 40),
        ];
        UsageHistoryEntry[] secondHistory =
        [
            new(Now.AddMinutes(-10), 100),
            new(Now.AddMinutes(1), 35),
        ];

        Assert.Empty(Evaluate(evaluator, Presentation(
            primaryRemaining: 40,
            secondaryRemaining: null,
            primaryReset: reset,
            primaryHistory: firstHistory), now: Now));
        var warning = Evaluate(evaluator, Presentation(
            primaryRemaining: 35,
            secondaryRemaining: null,
            primaryReset: reset,
            updatedAt: Now.AddMinutes(1),
            primaryHistory: secondHistory), now: Now.AddMinutes(1));

        var alert = Assert.Single(warning);
        Assert.StartsWith("forecast:", alert.Key, StringComparison.Ordinal);
        Assert.Contains("forecast may reach", alert.Message, StringComparison.Ordinal);
        Assert.Empty(Evaluate(evaluator, Presentation(
            primaryRemaining: 35,
            secondaryRemaining: null,
            primaryReset: reset,
            updatedAt: Now.AddMinutes(1),
            primaryHistory: secondHistory), now: Now.AddMinutes(1)));

        var predictiveBaselineEvaluator = new UsageAlertEvaluator();
        Assert.Empty(Evaluate(predictiveBaselineEvaluator, Presentation(
            primaryRemaining: 40,
            secondaryRemaining: null,
            primaryReset: reset,
            primaryHistory: secondHistory), now: Now.AddMinutes(1)));
        Assert.Empty(Evaluate(predictiveBaselineEvaluator, Presentation(
            primaryRemaining: 40,
            secondaryRemaining: null,
            primaryReset: reset,
            primaryHistory: secondHistory), now: Now.AddMinutes(1)));
    }

    private static IReadOnlyList<UsageAlert> Evaluate(
        UsageAlertEvaluator evaluator,
        UsagePresentation presentation,
        DateTimeOffset? now = null,
        UsageAlertOptions? options = null) =>
        evaluator.Evaluate(presentation, now ?? Now, RefreshInterval, options ?? EnabledOptions);

    private static UsagePresentation Presentation(
        double? primaryRemaining = 80,
        double? secondaryRemaining = 80,
        int primaryMinutes = 300,
        int secondaryMinutes = 10080,
        DateTimeOffset? primaryReset = null,
        DateTimeOffset? secondaryReset = null,
        DateTimeOffset? updatedAt = null,
        UsageDataSource source = UsageDataSource.AppServer,
        string? accountKey = "account-a",
        bool isLoading = false,
        string? defaultBucketId = "codex",
        IReadOnlyList<RateLimitBucket>? buckets = null,
        RateLimitResetCredits? resetCredits = null,
        IReadOnlyList<UsageHistoryEntry>? primaryHistory = null,
        IReadOnlyList<UsageHistoryEntry>? weeklyHistory = null)
    {
        var primary = primaryRemaining is { } primaryValue
            ? new RateLimitWindow(100 - primaryValue, primaryMinutes, primaryReset ?? Now.AddHours(4))
            : null;
        var secondary = secondaryRemaining is { } secondaryValue
            ? new RateLimitWindow(100 - secondaryValue, secondaryMinutes, secondaryReset ?? Now.AddDays(6))
            : null;
        var snapshot = new CodexUsageSnapshot(
            primary,
            secondary,
            "pro",
            null,
            resetCredits,
            updatedAt ?? Now,
            source,
            null,
            Buckets: buckets,
            AccountKey: accountKey,
            DefaultBucketId: defaultBucketId);
        return new UsagePresentation(
            snapshot,
            primaryHistory ?? Array.Empty<UsageHistoryEntry>(),
            weeklyHistory ?? Array.Empty<UsageHistoryEntry>(),
            new AdaptiveWeeklyUsageHistory([], null),
            LocalTokenUsageSnapshot.Unavailable,
            isLoading);
    }
}
