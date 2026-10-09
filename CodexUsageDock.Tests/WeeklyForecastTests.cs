using Xunit;
using Xunit.Abstractions;
using System.Text.Json;
using System.Globalization;

namespace CodexUsageDock.Tests;

public sealed class WeeklyForecastTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Reset = new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Reset.AddDays(-5);

    [Fact]
    public void ForecastBasisNamesQuotaHistoryCoverageSeparatelyFromTokenLogs()
    {
        var result = Analyze(Series(Now.AddHours(-1), Now, 80, 78), History(_ => 0.002));
        Assert.StartsWith("Quota forecast:", result.ForecastStatus, StringComparison.Ordinal);
        Assert.Contains("3 past weeks", result.ForecastStatus, StringComparison.Ordinal);
        Assert.Contains("100% history coverage", result.ForecastStatus, StringComparison.Ordinal);
        Assert.Contains("estimate only", result.ForecastStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("Historical influence grows", result.ForecastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortBurstDoesNotProduceAWeekProjection()
    {
        var result = Analyze(Series(Now.AddMinutes(-10), Now, 80, 78));

        Assert.Null(result.Forecast);
        Assert.Contains("30 minutes", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentPaceDoesNotDependOnOldContinuousIdleMeasurements()
    {
        var recent = Series(Now.AddHours(-6), Now, 90, 84);
        var withOldIdle = Series(Now.AddDays(-1), Now.AddHours(-6), 90, 90).Concat(recent).ToArray();

        Assert.Equal(Analyze(recent).Forecast!.EndsAt, Analyze(withOldIdle).Forecast!.EndsAt);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.5)]
    public void OlderConsumptionCannotMakeAnInsignificantRecentDecreaseForecastable(double recentDecrease)
    {
        var recent = Series(Now.AddHours(-6), Now, 90, 90 - recentDecrease);
        var samples = Series(Now.AddDays(-1), Now.AddHours(-6), 100, 90).Concat(recent).ToArray();

        Assert.Null(Analyze(samples).Forecast);
        Assert.NotNull(Analyze(samples, History(_ => 0.002)).Forecast);
    }

    [Theory]
    [InlineData(1, "Wed 16 Sep 06:45")]
    [InlineData(25, "Thu 17 Sep 06:45")]
    public void ChartAlternativeTextIncludesTheDateForNearAndDistantEstimates(int hoursAhead, string expected)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Forecast test zone", TimeSpan.FromMinutes(330), "Test", "Test");
        var samples = Series(Now.AddHours(-1), Now, 90, 80);
        var forecast = new UsageTrendForecast(Now.AddHours(hoursAhead).AddMinutes(7), 0, true);
        var chart = WeeklyUsageTrendChartRenderer.Create(samples, new RateLimitWindow(20, 10080, Reset), Now,
            TimeSpan.FromMinutes(15), forecast, culture: CultureInfo.InvariantCulture, timeZone: zone);

        Assert.NotNull(chart);
        Assert.Contains($"Estimated limit: {expected}; actual usage may differ.", chart.AltText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "01:15", "Wed 16 Sep 01:15")]
    [InlineData(25, "Thu 17 Sep", "Thu 17 Sep 01:15")]
    public void ExplicitEstimateDatesPreserveRoundingAndTheCompactDefault(int hoursAhead, string compact, string dated)
    {
        var estimate = Now.AddHours(hoursAhead).AddMinutes(7);
        Assert.Equal(compact, UsageTrendAnalyzer.FormatWeeklyLimitEstimate(estimate, Now, CultureInfo.InvariantCulture, TimeZoneInfo.Utc));
        Assert.Equal(dated, UsageTrendAnalyzer.FormatWeeklyLimitEstimate(estimate, Now, CultureInfo.InvariantCulture, TimeZoneInfo.Utc, includeDate: true));
    }

    [Fact]
    public void EightMinutesOfHistoryDoNotCountAsEightUsableWeeks()
    {
        var cycles = Enumerable.Range(1, 8)
            .Select(index => new AdaptiveWeeklyUsageCycle(Reset.AddDays(-7 * index), 10080, 1, 0.01, []))
            .ToArray();
        var samples = Series(Now.AddHours(-1), Now, 80, 78);
        var result = Analyze(samples, new(cycles, null));

        Assert.Equal(Analyze(samples).Forecast!.EndsAt, result.Forecast!.EndsAt);
        Assert.Contains("insufficient", result.ForecastStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepresentativeHistoryCanForecastAfterObservedIdleTime()
    {
        var result = Analyze(Series(Now.AddHours(-1), Now, 80, 80), History(_ => 0.002));

        Assert.NotNull(result.Forecast);
        Assert.InRange(result.Forecast.RemainingPercent, 60, 79);
        Assert.Contains("3 past weeks", result.ForecastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void LearnedQuietPeriodsEventuallyOverrideABurst()
    {
        var result = Analyze(Series(Now.AddHours(-1), Now, 81.2, 80), History(_ => 0));

        Assert.NotNull(result.Forecast);
        Assert.False(result.Forecast.ReachesLimitBeforeReset);
        Assert.InRange(result.Forecast.RemainingPercent, 70, 80);
    }

    [Fact]
    public void MissingNightMeasurementsAreNotRepresentativeFullDayHistory()
    {
        var cycles = History(_ => 0.002).CompletedCycles.Select(cycle => cycle with
        {
            Buckets = cycle.Buckets.Where(bucket => bucket.Index % 4 != 0).ToArray(),
            ObservedMinutes = 7560,
            ConsumedPercent = 15.12,
        }).ToArray();
        var samples = Series(Now.AddHours(-1), Now, 80, 78);

        Assert.Equal(Analyze(samples).Forecast!.EndsAt, Analyze(samples, new(cycles, null)).Forecast!.EndsAt);
    }

    [Fact]
    public void OldAndFutureProfilesCannotInfluenceTheForecast()
    {
        var cycles = History(_ => 0).CompletedCycles.Select((cycle, index) => cycle with
        {
            ResetsAt = index == 0 ? Now.AddDays(1) : Reset.AddDays(-100 - index * 7),
        }).ToArray();
        var samples = Series(Now.AddHours(-1), Now, 80, 78);

        Assert.Equal(Analyze(samples).Forecast!.EndsAt, Analyze(samples, new(cycles, null)).Forecast!.EndsAt);
    }

    [Fact]
    public void FreshMeasurementAfterAGapUsesHistoryWithoutInterpretingTheGapAsConsumption()
    {
        var samples = Series(Now.AddHours(-2), Now.AddHours(-1), 90, 85)
            .Append(new UsageHistoryEntry(Now, 80)).ToArray();

        var result = Analyze(samples, History(_ => 0.002));
        var latestOnly = Analyze([samples[^1]], History(_ => 0.002));

        Assert.NotNull(result.Forecast);
        Assert.Equal(latestOnly.Forecast!.RemainingPercent, result.Forecast.RemainingPercent, precision: 8);
        Assert.Single(result.History);
        Assert.Contains("historical usage only", result.ForecastStatus, StringComparison.Ordinal);
        Assert.Contains("0/30", result.ForecastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void OneFreshMeasurementCanStartAHistoryOnlyWeeklyForecast()
    {
        var result = Analyze([new(Now, 80)], History(_ => 0.002));

        Assert.NotNull(result.Forecast);
        Assert.Equal(65.6, result.Forecast.RemainingPercent, precision: 8);
        Assert.Contains("historical usage only", result.ForecastStatus, StringComparison.Ordinal);
        Assert.Contains("3 past weeks", result.ForecastStatus, StringComparison.Ordinal);
        Assert.Contains("0/30", result.ForecastStatus, StringComparison.Ordinal);
        Assert.Contains("observed history", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortBurstUsesHistoricalPaceUntilThereAreThirtyMinutesOfMeasurements()
    {
        var samples = Series(Now.AddMinutes(-10), Now, 90, 80);
        var result = Analyze(samples, History(_ => 0.002));
        var latestOnly = Analyze([samples[^1]], History(_ => 0.002));

        Assert.NotNull(result.Forecast);
        Assert.Equal(latestOnly.Forecast!.RemainingPercent, result.Forecast.RemainingPercent, precision: 8);
        Assert.Contains("10/30", result.ForecastStatus, StringComparison.Ordinal);
        Assert.Contains("historical usage only", result.ForecastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowanceRestorationStartsAHistoryOnlyForecastFromTheNewAllowance()
    {
        var samples = Series(Now.AddHours(-1), Now.AddMinutes(-5), 10, 5)
            .Append(new UsageHistoryEntry(Now, 100)).ToArray();
        var result = Analyze(samples, History(_ => 0.002));

        Assert.NotNull(result.Forecast);
        Assert.Single(result.History);
        Assert.Equal(85.6, result.Forecast.RemainingPercent, precision: 8);
        Assert.Contains("historical usage only", result.ForecastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapDoesNotBypassFreshnessAvailabilityOrTheAdaptivePreference()
    {
        var samples = new UsageHistoryEntry[] { new(Now, 80) };
        var history = History(_ => 0.002);

        Assert.Null(Analyze(samples).Forecast);
        Assert.Null(Analyze([], history).Forecast);
        Assert.Null(Analyze([new(Now.AddMinutes(-6), 80)], history).Forecast);
        Assert.Null(UsageTrendAnalyzer.Analyze(samples, Reset.AddDays(-7), Reset, Now, false,
            TimeSpan.FromMinutes(5), true, history).Forecast);
        Assert.Null(UsageTrendAnalyzer.Analyze(samples, Reset.AddDays(-7), Reset, Now, true,
            TimeSpan.FromMinutes(5), false, history).Forecast);
    }

    [Fact]
    public void BootstrapRejectsInsufficientOutdatedFutureAndMalformedHistoricalProfiles()
    {
        var history = History(_ => 0.002);
        var samples = new UsageHistoryEntry[] { new(Now, 80) };
        var invalid = new[]
        {
            history.CompletedCycles.Select(cycle => cycle with { ObservedMinutes = 1, Buckets = [] }).ToArray(),
            history.CompletedCycles.Select(cycle => cycle with { ResetsAt = Now.AddDays(-100) }).ToArray(),
            history.CompletedCycles.Select(cycle => cycle with { ResetsAt = Now.AddDays(1) }).ToArray(),
            history.CompletedCycles.Select(cycle => cycle with { ConsumedPercent = double.NaN }).ToArray(),
            history.CompletedCycles.Select(cycle => cycle with { Buckets = [] }).ToArray(),
            history.CompletedCycles.Select(cycle => cycle with
            {
                Buckets = cycle.Buckets.Where(bucket => bucket.Index % 4 != 0).ToArray(),
                ObservedMinutes = 7560,
                ConsumedPercent = 15.12,
            }).ToArray(),
        };

        foreach (var cycles in invalid)
        {
            Assert.Null(Analyze(samples, new(cycles, null)).Forecast);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BootstrapRequiresAnActiveWeeklyWindow(int resetMinutesFromNow)
    {
        var reset = Now.AddMinutes(resetMinutesFromNow);
        var result = UsageTrendAnalyzer.Analyze([new(Now, 80)], reset.AddDays(-7), reset, Now, true,
            TimeSpan.FromMinutes(5), true, History(_ => 0.002, reset));

        Assert.Null(result.Forecast);
    }

    [Fact]
    public void BootstrapRequiresTheLatestMeasurementToBelongToTheCurrentWeeklyWindow()
    {
        var history = History(_ => 0.002);

        Assert.Null(Analyze([new(Now, 80, Reset.AddHours(-1), 10080)], history).Forecast);
        Assert.Null(Analyze([new(Now, 80, Reset, 300)], history).Forecast);
        Assert.NotNull(Analyze([new(Now, 80, Reset.AddSeconds(3), 10080)], history).Forecast);
    }

    [Fact]
    public void ThirtyMinutesOfMeasurementsResumeRecentPaceAdaptation()
    {
        var samples = Series(Now.AddMinutes(-30), Now, 90, 80);
        var result = Analyze(samples, History(_ => 0.002));

        Assert.NotNull(result.Forecast);
        Assert.Contains("recent 30 min", result.ForecastStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("historical usage only", result.ForecastStatus, StringComparison.Ordinal);
        Assert.True(result.Forecast.RemainingPercent < Analyze([samples[^1]], History(_ => 0.002)).Forecast!.RemainingPercent);
    }

    [Fact]
    public void EarlyResetDoesNotReplayOverlappingMeasurementsFromThePreviousCycle()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("early-reset-forecast.json");
        var store = new AdaptiveWeeklyUsageStore(path);
        var previous = Series(Now.AddMinutes(-30), Now.AddMinutes(-5), 90, 85)
            .Select(sample => sample with { ResetsAt = Reset, WindowMinutes = 10080 }).ToArray();
        store.Record(new RateLimitWindow(15, 10080, Reset), previous, TimeSpan.FromMinutes(15));
        var earlyReset = Reset.AddDays(1);
        var first = new UsageHistoryEntry(Now, 80, earlyReset, 10080);
        store.Record(new RateLimitWindow(20, 10080, earlyReset), previous.Append(first).ToArray(),
            TimeSpan.FromMinutes(15));

        Assert.Equal(5, Assert.Single(store.Snapshot.CompletedCycles).ConsumedPercent, precision: 8);
        Assert.Equal(0, store.Snapshot.ActiveCycle!.ObservedMinutes);
        Assert.Equal(0, store.Snapshot.ActiveCycle.ConsumedPercent);

        var next = new UsageHistoryEntry(Now.AddMinutes(5), 79, earlyReset, 10080);
        store.Record(new RateLimitWindow(21, 10080, earlyReset), previous.Append(first).Append(next).ToArray(),
            TimeSpan.FromMinutes(15));
        var restored = new AdaptiveWeeklyUsageStore(path);
        Assert.Equal(5, restored.Snapshot.ActiveCycle!.ObservedMinutes, precision: 8);
        Assert.Equal(1, restored.Snapshot.ActiveCycle.ConsumedPercent, precision: 8);
    }

    [Fact]
    public void LearningRejectsKnownNonWeeklyMeasurementsAndMismatchedSavedBaselines()
    {
        using var environment = new TestEnvironment();
        var path = environment.PathFor("mismatched-forecast.json");
        var state = new AdaptiveWeeklyUsageState([], new(Reset, 10080, 0, 0, []),
            new(Now.AddMinutes(-5), 90, Reset.AddHours(-1), 10080), true);
        File.WriteAllText(path, JsonSerializer.Serialize(state, AdaptiveWeeklyUsageJsonContext.Default.AdaptiveWeeklyUsageState));
        var store = new AdaptiveWeeklyUsageStore(path);
        var samples = new UsageHistoryEntry[]
        {
            new(Now.AddMinutes(-5), 90, Reset, 300),
            new(Now, 80, Reset, 10080),
        };
        store.Record(new RateLimitWindow(20, 10080, Reset), samples, TimeSpan.FromMinutes(15));

        Assert.Equal(0, store.Snapshot.ActiveCycle!.ObservedMinutes);
        Assert.Equal(0, store.Snapshot.ActiveCycle.ConsumedPercent);

        store.Record(new RateLimitWindow(21, 10080, Reset),
            samples.Append(new UsageHistoryEntry(Now.AddMinutes(5), 79, Reset, 10080)).ToArray(),
            TimeSpan.FromMinutes(15));
        Assert.Equal(5, store.Snapshot.ActiveCycle.ObservedMinutes, precision: 8);
        Assert.Equal(1, store.Snapshot.ActiveCycle.ConsumedPercent, precision: 8);
    }

    [Fact]
    public void ShiftedResetPatternsFallBackToTheRepresentativeAverage()
    {
        var patterned = History(index => index % 4 == 0 ? 0.008 : 0).CompletedCycles
            .Select(cycle => cycle with { ResetsAt = cycle.ResetsAt.AddDays(-1) }).ToArray();
        var flat = History(_ => 0.002).CompletedCycles
            .Select(cycle => cycle with { ResetsAt = cycle.ResetsAt.AddDays(-1) }).ToArray();
        var samples = Series(Now.AddHours(-1), Now, 80, 78);

        Assert.Equal(Analyze(samples, new(flat, null)).Forecast!.RemainingPercent,
            Analyze(samples, new(patterned, null)).Forecast!.RemainingPercent, precision: 8);
    }

    [Fact]
    public void PartialCyclesHaveLessInfluenceThanFullyObservedCycles()
    {
        var complete = History(_ => 0);
        var partial = new AdaptiveWeeklyUsageHistory(complete.CompletedCycles.Select(cycle => cycle with
        {
            ObservedMinutes = 5040,
            Buckets = cycle.Buckets.Select(bucket => bucket with { ObservedMinutes = 180 }).ToArray(),
        }).ToArray(), null);
        var samples = Series(Now.AddHours(-1), Now, 81.2, 80);

        Assert.True(Analyze(samples, partial).Forecast!.RemainingPercent < Analyze(samples, complete).Forecast!.RemainingPercent);
    }

    [Fact]
    public void BudgetIsAvailableWithoutAForecastAndSuppressedWhenStale()
    {
        var snapshot = CodexUsageSnapshot.Loading with
        {
            Source = UsageDataSource.AppServer,
            Secondary = new RateLimitWindow(55, 10080, Now.AddDays(3)),
            UpdatedAt = Now,
        };
        using var fresh = JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(snapshot, Now, false, [], [], TimeSpan.FromMinutes(1)));
        Assert.Contains("15 percentage points per day", fresh.RootElement.GetProperty("weeklyBudget").GetString(), StringComparison.Ordinal);
        using var stale = JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(snapshot, Now.AddHours(1), false, [], [], TimeSpan.FromMinutes(1)));
        Assert.Contains("unavailable", stale.RootElement.GetProperty("weeklyBudget").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyEstimateDoesNotDisplayMinutePrecisionSeveralDaysAhead()
    {
        var local = Now.ToLocalTime().Date.AddDays(2).AddHours(14).AddMinutes(37);
        var estimate = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));

        Assert.DoesNotContain("14:37", UsageTrendAnalyzer.FormatWeeklyLimitEstimate(estimate, Now), StringComparison.Ordinal);
        Assert.Equal("12:15", UsageTrendAnalyzer.FormatWeeklyLimitEstimate(
            new DateTimeOffset(local.AddDays(-2).Date.AddHours(12).AddMinutes(15), estimate.Offset),
            new DateTimeOffset(local.AddDays(-2).Date.AddHours(11), estimate.Offset)));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(24)]
    [InlineData(0)]
    public void RollingEvaluationImprovesOnCurrentPaceForARepeatedUsagePattern(int horizonHours)
    {
        // Every origin uses only the preceding three complete weeks and already-observed current-week values.
        // This is a deterministic benchmark, not a claim of accuracy on users' future workloads.
        static double Rate(int index) => (index % 4) switch { 0 => 0, 1 => 0.0005, 2 => 0.006, _ => 0.0015 };
        var start = Reset.AddDays(-7);
        var history = History(Rate);
        var adaptiveError = 0d;
        var baselineError = 0d;
        var forecasts = 0;
        for (var bucket = 1; bucket < 28; bucket++)
        {
            var origin = start.AddHours(bucket * 6);
            var target = horizonHours == 0 ? Reset : origin.AddHours(horizonHours);
            if (target > Reset)
            {
                continue;
            }

            var remaining = 100 - Enumerable.Range(0, bucket).Sum(index => Rate(index) * 360);
            var latest = new UsageHistoryEntry(origin, remaining);
            var recentRate = Rate(bucket - 1);
            var adaptive = AdaptiveWeeklyForecast.Project(latest, start, Reset, recentRate, true, history).Forecast;
            var actual = 100 - Enumerable.Range(0, (int)((target - start).TotalHours / 6)).Sum(index => Rate(index) * 360);
            var predicted = adaptive.Points!.Last(point => point.RecordedAt <= target).RemainingPercent;
            var baseline = Math.Max(0, remaining - recentRate * (target - origin).TotalMinutes);
            adaptiveError += Math.Abs(actual - predicted);
            baselineError += Math.Abs(actual - baseline);
            forecasts++;
        }

        output.WriteLine($"Horizon {horizonHours} h (0 = reset), {forecasts} origins: MAE adaptive {adaptiveError / forecasts:0.000} pp; current pace {baselineError / forecasts:0.000} pp.");
        Assert.True(adaptiveError < baselineError, $"Adaptive error {adaptiveError} should improve on baseline {baselineError}.");
    }

    [Theory]
    [InlineData(6)]
    [InlineData(24)]
    [InlineData(0)]
    public void ChronologicalBootstrapRecoversARepeatedPatternWithoutCurrentPace(int horizonHours)
    {
        // Each fresh week has one measurement. Only the three completed preceding weeks are available.
        // This repeated-pattern fixture does not establish accuracy for changing real workloads.
        static double Rate(int index) => (index % 4) switch { 0 => 0, 1 => 0.0005, 2 => 0.006, _ => 0.0015 };
        var error = 0d;
        for (var week = 0; week < 3; week++)
        {
            var reset = Reset.AddDays(7 * week);
            var origin = reset.AddDays(-7);
            var history = History(Rate, reset);
            Assert.All(history.CompletedCycles, cycle => Assert.True(cycle.ResetsAt <= origin));
            var result = UsageTrendAnalyzer.Analyze([new(origin, 100, reset, 10080)], origin, reset,
                origin, true, TimeSpan.FromMinutes(5), true, history);
            var target = horizonHours == 0 ? reset : origin.AddHours(horizonHours);
            var actual = 100 - Enumerable.Range(0, (int)((target - origin).TotalHours / 6))
                .Sum(index => Rate(index) * 360);

            Assert.NotNull(result.Forecast);
            var predicted = result.Forecast.Points!.Last(point => point.RecordedAt <= target).RemainingPercent;
            error += Math.Abs(actual - predicted);
        }

        output.WriteLine($"Bootstrap horizon {horizonHours} h (0 = reset), 3 chronological origins: MAE {error / 3:0.000} pp.");
        Assert.InRange(error / 3, 0, 0.000001);
    }

    internal static UsageHistoryEntry[] Series(DateTimeOffset start, DateTimeOffset end, double first, double last)
    {
        var count = (int)Math.Ceiling((end - start).TotalMinutes / 5);
        return Enumerable.Range(0, count + 1).Select(index => new UsageHistoryEntry(
            start + TimeSpan.FromTicks((end - start).Ticks * index / count),
            first + (last - first) * index / count)).ToArray();
    }

    internal static AdaptiveWeeklyUsageHistory History(Func<int, double> rate, DateTimeOffset? reset = null)
    {
        var buckets = Enumerable.Range(0, 28).Select(index => new AdaptiveWeeklyUsageBucket(index, 360, 360 * rate(index))).ToArray();
        return new(Enumerable.Range(1, 3).Select(index => new AdaptiveWeeklyUsageCycle(
            (reset ?? Reset).AddDays(-7 * index), 10080, 10080, buckets.Sum(bucket => bucket.ConsumedPercent), buckets)).ToArray(), null);
    }

    private static UsageTrendAnalyzer.TrendAnalysis Analyze(UsageHistoryEntry[] samples, AdaptiveWeeklyUsageHistory? history = null) =>
        UsageTrendAnalyzer.Analyze(samples, Reset.AddDays(-7), Reset, Now, true, TimeSpan.FromMinutes(5), history is not null, history);
}
