using Xunit;

namespace CodexUsageDock.Tests;

public sealed class UsagePlanningTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    [Fact]
    public void FreshPlanReportsRemainingQuotaAndRatesUntilTheEarlierBoundary()
    {
        var desiredEnd = Now.AddHours(5);
        var plan = UsagePlanner.Plan(
            Presentation(
                primaryRemaining: 50,
                secondaryRemaining: 80,
                primaryReset: Now.AddHours(2),
                secondaryReset: Now.AddDays(4)),
            Now,
            RefreshInterval,
            desiredEnd,
            remainingWorkdays: 3);

        Assert.True(plan.IsAvailable);
        Assert.Equal(50, plan.Primary!.RemainingPercent);
        Assert.Equal(TimeSpan.FromHours(2), plan.Primary.Horizon);
        Assert.Equal(Now.AddHours(2), plan.Primary.HorizonEnd);
        Assert.True(plan.Primary.ResetsBeforeDesiredEnd);
        Assert.Equal(1, plan.Primary.Workdays);
        Assert.Equal(25, plan.Primary.AvailablePointsPerHour, precision: 8);
        Assert.Equal(50, plan.Primary.AvailablePointsPerWorkday, precision: 8);
        Assert.Contains("resets before the desired end", plan.Primary.HorizonMessage, StringComparison.Ordinal);

        Assert.Equal(80, plan.Weekly!.RemainingPercent);
        Assert.Equal(TimeSpan.FromHours(5), plan.Weekly.Horizon);
        Assert.False(plan.Weekly.ResetsBeforeDesiredEnd);
        Assert.Equal(3, plan.Weekly.Workdays);
        Assert.Equal(80 / 3d / 5d, plan.Weekly.AvailablePointsPerHour, precision: 8);
        Assert.Contains("not a guarantee", plan.Disclaimer, StringComparison.Ordinal);
        Assert.Contains("not a guarantee", plan.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyWorkdayAssumptionAffectsDailyAndHourlyBudget()
    {
        var desiredEnd = Now.AddHours(5);
        var fiveDayPlan = UsagePlanner.Plan(
            Presentation(primaryRemaining: null, secondaryRemaining: 50, secondaryReset: Now.AddDays(7)),
            Now,
            RefreshInterval,
            desiredEnd,
            remainingWorkdays: 5);
        var oneDayPlan = UsagePlanner.Plan(
            Presentation(primaryRemaining: null, secondaryRemaining: 50, secondaryReset: Now.AddDays(7)),
            Now,
            RefreshInterval,
            desiredEnd,
            remainingWorkdays: 1);

        Assert.Equal(10, fiveDayPlan.Weekly!.AvailablePointsPerWorkday, precision: 8);
        Assert.Equal(2, fiveDayPlan.Weekly.AvailablePointsPerHour, precision: 8);
        Assert.Equal(50, oneDayPlan.Weekly!.AvailablePointsPerWorkday, precision: 8);
        Assert.Equal(10, oneDayPlan.Weekly.AvailablePointsPerHour, precision: 8);
    }

    [Fact]
    public void InvalidEndOrWorkdayCountReturnsAnExplanation()
    {
        var presentation = Presentation();

        var now = UsagePlanner.Plan(presentation, Now, RefreshInterval, Now);
        var zeroDays = UsagePlanner.Plan(presentation, Now, RefreshInterval, Now.AddHours(1), 0);
        var eightDays = UsagePlanner.Plan(presentation, Now, RefreshInterval, Now.AddHours(1), 8);

        Assert.False(now.IsAvailable);
        Assert.Contains("desired end must be after now", now.Status, StringComparison.Ordinal);
        Assert.False(zeroDays.IsAvailable);
        Assert.Contains("between 1 and 7", zeroDays.Status, StringComparison.Ordinal);
        Assert.False(eightDays.IsAvailable);
        Assert.Contains("between 1 and 7", eightDays.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleFutureUnattributedAndBlockedDataPausePlanning()
    {
        var desiredEnd = Now.AddHours(2);
        var cases = new[]
        {
            (Presentation(updatedAt: Now.AddMinutes(-6)), "usage data is stale"),
            (Presentation(updatedAt: Now.AddMinutes(1)), "timestamp is in the future"),
            (Presentation(accountKey: null), "account identity is unavailable"),
            (Presentation(ordinaryUsageAllowed: false), "ordinary usage is currently blocked"),
        };

        foreach (var (presentation, expected) in cases)
        {
            var plan = UsagePlanner.Plan(presentation, Now, RefreshInterval, desiredEnd);
            Assert.False(plan.IsAvailable);
            Assert.Contains(expected, plan.Status, StringComparison.Ordinal);
            Assert.Empty(plan.Windows);
        }
    }

    [Fact]
    public void LastConfirmedAndLoadingDataAreNotTreatedAsFreshPlans()
    {
        var desiredEnd = Now.AddHours(2);
        var lastConfirmed = UsagePlanner.Plan(
            Presentation(source: UsageDataSource.LastConfirmed),
            Now,
            RefreshInterval,
            desiredEnd);
        var loading = UsagePlanner.Plan(
            Presentation(isLoading: true),
            Now,
            RefreshInterval,
            desiredEnd);

        Assert.False(lastConfirmed.IsAvailable);
        Assert.Contains("last confirmed", lastConfirmed.Status, StringComparison.Ordinal);
        Assert.False(loading.IsAvailable);
        Assert.Contains("loading", loading.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredPrimaryCanLeaveAValidWeeklyPlanAndExpiredBothPause()
    {
        var desiredEnd = Now.AddDays(1);
        var primaryExpired = UsagePlanner.Plan(
            Presentation(primaryReset: Now.AddMinutes(-1), secondaryReset: Now.AddDays(4)),
            Now,
            RefreshInterval,
            desiredEnd);
        var bothExpired = UsagePlanner.Plan(
            Presentation(primaryReset: Now.AddMinutes(-1), secondaryReset: Now.AddMinutes(-1)),
            Now,
            RefreshInterval,
            desiredEnd);

        Assert.True(primaryExpired.IsAvailable);
        Assert.Null(primaryExpired.Primary);
        Assert.NotNull(primaryExpired.Weekly);
        Assert.False(bothExpired.IsAvailable);
        Assert.Contains("no valid", bothExpired.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ForecastUsesOnlyAUsableCurrentPaceAndReportsUnavailableEvidenceWhenInsufficient()
    {
        var reset = Now.AddHours(4);
        UsageHistoryEntry[] history =
        [
            new(Now.AddMinutes(-10), 100),
            new(Now, 50),
        ];
        var plan = UsagePlanner.Plan(
            Presentation(
                primaryRemaining: 50,
                secondaryRemaining: null,
                primaryReset: reset,
                primaryHistory: history),
            Now,
            RefreshInterval,
            Now.AddHours(1));

        Assert.True(plan.IsAvailable);
        Assert.True(plan.Primary!.Forecast.IsAvailable);
        Assert.True(plan.Primary.Forecast.ReachesLimitBeforeReset);
        Assert.Contains("current pace", plan.Primary.Forecast.Status, StringComparison.Ordinal);
        Assert.Equal(2, plan.Primary.Evidence.MeasurementCount);
        Assert.Equal(2, plan.Primary.Evidence.SegmentMeasurementCount);
        Assert.False(plan.Primary.Backtest.IsAvailable);

        var insufficient = UsagePlanner.Plan(
            Presentation(
                primaryRemaining: 50,
                secondaryRemaining: null,
                primaryReset: reset,
                primaryHistory: [new(Now, 50)]),
            Now,
            RefreshInterval,
            Now.AddHours(1));
        Assert.False(insufficient.Primary!.Forecast.IsAvailable);
        Assert.Contains("another measurement", insufficient.Primary.Forecast.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void GapsBreakTheContinuousSegmentAndPauseForecast()
    {
        UsageHistoryEntry[] history =
        [
            new(Now.AddMinutes(-40), 100),
            new(Now.AddMinutes(-20), 90),
            new(Now, 80),
        ];
        var plan = UsagePlanner.Plan(
            Presentation(
                primaryRemaining: 80,
                secondaryRemaining: null,
                primaryHistory: history),
            Now,
            RefreshInterval,
            Now.AddHours(1));

        Assert.Equal(3, plan.Primary!.Evidence.MeasurementCount);
        Assert.Equal(1, plan.Primary.Evidence.SegmentMeasurementCount);
        Assert.False(plan.Primary.Forecast.IsAvailable);
        Assert.Contains("another measurement", plan.Primary.Forecast.Status, StringComparison.Ordinal);
        Assert.False(plan.Primary.Backtest.IsAvailable);
    }

    [Fact]
    public void EvidenceDescribesMeasurementsSegmentsAdaptiveCyclesAndItsLimit()
    {
        UsageHistoryEntry[] history =
        [
            new(Now.AddMinutes(-20), 100),
            new(Now.AddMinutes(-10), 90),
            new(Now, 80),
        ];
        var adaptive = new AdaptiveWeeklyUsageHistory(
            [new AdaptiveWeeklyUsageCycle(Now.AddDays(-7), 10080, 60, 10, [])],
            new AdaptiveWeeklyUsageCycle(Now.AddDays(7), 10080, 60, 10, []));
        var plan = UsagePlanner.Plan(
            Presentation(
                primaryRemaining: null,
                secondaryRemaining: 80,
                weeklyHistory: history,
                adaptiveHistory: adaptive),
            Now,
            RefreshInterval,
            Now.AddHours(1));

        var evidence = plan.Weekly!.Evidence;
        Assert.Equal(3, evidence.MeasurementCount);
        Assert.Equal(TimeSpan.FromMinutes(20), evidence.MeasurementSpan);
        Assert.Equal(3, evidence.SegmentMeasurementCount);
        Assert.Equal(TimeSpan.FromMinutes(20), evidence.SegmentSpan);
        Assert.Equal(2, evidence.AdaptiveCycleCount);
        Assert.Contains("3 measurements", evidence.Summary, StringComparison.Ordinal);
        Assert.Contains("continuous segment", evidence.Summary, StringComparison.Ordinal);
        Assert.Contains("adaptive weekly cycles: 2", evidence.Summary, StringComparison.Ordinal);
        Assert.Contains("not a calibrated reliability probability", evidence.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BacktestHoldsOutTheLatestMeasurementAndDoesNotTrainOnIt()
    {
        UsageHistoryEntry[] firstHistory =
        [
            new(Now.AddMinutes(-20), 100),
            new(Now.AddMinutes(-10), 90),
            new(Now, 70),
        ];
        UsageHistoryEntry[] changedHoldout =
        [
            new(Now.AddMinutes(-20), 100),
            new(Now.AddMinutes(-10), 90),
            new(Now, 60),
        ];
        var first = UsagePlanner.Plan(
            Presentation(primaryRemaining: 70, secondaryRemaining: null, primaryHistory: firstHistory),
            Now,
            RefreshInterval,
            Now.AddHours(1)).Primary!.Backtest;
        var second = UsagePlanner.Plan(
            Presentation(primaryRemaining: 60, secondaryRemaining: null, primaryHistory: changedHoldout),
            Now,
            RefreshInterval,
            Now.AddHours(1)).Primary!.Backtest;

        Assert.True(first.IsAvailable);
        Assert.Equal(2, first.TrainingSampleCount);
        Assert.Equal(Now, first.HeldOutAt);
        Assert.Equal(80, first.PredictedRemainingPercent!.Value, precision: 8);
        Assert.Equal(70, first.ActualRemainingPercent!.Value, precision: 8);
        Assert.Equal(10, first.AbsoluteErrorPercentagePoints!.Value, precision: 8);
        Assert.Equal(first.PredictedRemainingPercent, second.PredictedRemainingPercent);
        Assert.Equal(20, second.AbsoluteErrorPercentagePoints!.Value, precision: 8);
        Assert.Contains("excluded from the preceding-pace calculation", first.Status, StringComparison.Ordinal);
    }

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
        bool? ordinaryUsageAllowed = null,
        IReadOnlyList<UsageHistoryEntry>? primaryHistory = null,
        IReadOnlyList<UsageHistoryEntry>? weeklyHistory = null,
        AdaptiveWeeklyUsageHistory? adaptiveHistory = null)
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
            null,
            updatedAt ?? Now,
            source,
            null,
            AccountKey: accountKey,
            OrdinaryUsageAllowed: ordinaryUsageAllowed,
            DefaultBucketId: "codex");
        return new UsagePresentation(
            snapshot,
            primaryHistory ?? Array.Empty<UsageHistoryEntry>(),
            weeklyHistory ?? Array.Empty<UsageHistoryEntry>(),
            adaptiveHistory ?? new AdaptiveWeeklyUsageHistory([], null),
            LocalTokenUsageSnapshot.Unavailable,
            isLoading);
    }
}
