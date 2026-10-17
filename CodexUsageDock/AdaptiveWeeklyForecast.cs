using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageDock;

internal sealed record AdaptiveWeeklyUsageBucket(int Index, double ObservedMinutes, double ConsumedPercent);

internal sealed record AdaptiveWeeklyUsageCycle(
    DateTimeOffset ResetsAt,
    int WindowMinutes,
    double ObservedMinutes,
    double ConsumedPercent,
    AdaptiveWeeklyUsageBucket[] Buckets);

internal sealed record AdaptiveWeeklyUsageState(
    AdaptiveWeeklyUsageCycle[] CompletedCycles,
    AdaptiveWeeklyUsageCycle? ActiveCycle,
    UsageHistoryEntry? LastSample,
    bool IsInitialized);

internal sealed record AdaptiveWeeklyUsageHistory(
    AdaptiveWeeklyUsageCycle[] CompletedCycles,
    AdaptiveWeeklyUsageCycle? ActiveCycle,
    HourlyUsageHistory? Hourly = null);

internal sealed record AdaptiveWeeklyForecastProjection(UsageTrendForecast Forecast, string Status, bool UsesHistory = false);

internal static class AdaptiveWeeklyForecast
{
    private const int MinimumPatternCycles = 3;
    private const double MinimumCoverage = 0.5;
    internal const int HistoryHalfLifeDays = 28;
    private static readonly TimeSpan MaximumHistoryAge = TimeSpan.FromDays(AdaptiveWeeklyUsageStore.RetentionDays);
    private static readonly TimeSpan RecentInfluenceDuration = TimeSpan.FromHours(3);

    internal static AdaptiveWeeklyForecastProjection Project(
        UsageHistoryEntry latest,
        DateTimeOffset windowStart,
        DateTimeOffset resetsAt,
        double currentRatePerMinute,
        bool enabled,
        AdaptiveWeeklyUsageHistory? history,
        TimeSpan? recentDuration = null)
    {
        if (enabled && history?.Hourly is not null)
        {
            // Hourly history supplies startup/idle estimates; established recent pace stays authoritative.
            var available = ProjectFromHistory(latest, windowStart, resetsAt, history) is not null;
            return new(ProjectAtCurrentRate(latest, resetsAt, currentRatePerMinute),
                $"Quota forecast: {FormatRecentBasis(recentDuration)} only. "
                + (available ? "Saved quota history is available for startup; estimate only."
                    : $"Historical startup needs {HourlyUsageForecast.MinimumObservedHours} measured hours across {HourlyUsageForecast.MinimumObservedDates} dates."));
        }

        var profiles = SelectProfiles(windowStart, enabled ? history : null);
        if (profiles.Length == 0)
        {
            var reason = enabled ? " Weekly history is insufficient or outdated." : string.Empty;
            return new(ProjectAtCurrentRate(latest, resetsAt, currentRatePerMinute),
                $"Quota forecast: {FormatRecentBasis(recentDuration)} only.{reason}");
        }

        return ProjectUsingProfiles(latest, windowStart, resetsAt, currentRatePerMinute, profiles, recentDuration);
    }

    internal static AdaptiveWeeklyForecastProjection? ProjectFromHistory(
        UsageHistoryEntry latest,
        DateTimeOffset windowStart,
        DateTimeOffset resetsAt,
        AdaptiveWeeklyUsageHistory history)
    {
        if (history.Hourly is not null && HourlyUsageForecast.Project(latest, resetsAt, history) is { } hourly)
        {
            return hourly;
        }

        var profiles = SelectProfiles(windowStart, history);
        return profiles.Length == 0
            ? null
            : ProjectUsingProfiles(latest, windowStart, resetsAt, null, profiles, null);
    }

    private static AdaptiveWeeklyUsageCycle[] SelectProfiles(
        DateTimeOffset windowStart, AdaptiveWeeklyUsageHistory? history) =>
        history is null ? [] : history.CompletedCycles
            .Where(cycle => IsRepresentative(cycle)
                && cycle.ResetsAt <= windowStart + WeeklyAllowanceRestoration.ResetCycleTolerance
                && windowStart - cycle.ResetsAt <= MaximumHistoryAge)
            .OrderByDescending(cycle => cycle.ResetsAt)
            .DistinctBy(cycle => cycle.ResetsAt)
            .Take(AdaptiveWeeklyUsageStore.MaximumCompletedCycles)
            .ToArray();

    private static AdaptiveWeeklyForecastProjection ProjectUsingProfiles(
        UsageHistoryEntry latest,
        DateTimeOffset windowStart,
        DateTimeOffset resetsAt,
        double? currentRatePerMinute,
        AdaptiveWeeklyUsageCycle[] profiles,
        TimeSpan? recentDuration)
    {
        var totalWeight = profiles.Sum(cycle => Weight(cycle, windowStart));
        var coverage = profiles.Sum(cycle => Coverage(cycle) * Weight(cycle, windowStart)) / totalWeight;
        // Limited coverage or history depth slows the transition from recent pace to the historical baseline.
        var historyInfluence = coverage * Math.Min(1, totalWeight / MinimumPatternCycles);
        var historicalRate = WeightedMedian(profiles.Select(cycle =>
            (Rate: cycle.ConsumedPercent / cycle.ObservedMinutes, Weight: Weight(cycle, windowStart))));
        var points = new List<UsageTrendForecastPoint>();
        var cursor = latest.RecordedAt;
        var remaining = latest.RemainingPercent;
        while (cursor < resetsAt && remaining > 0)
        {
            var index = GetBucketIndex(cursor, windowStart);
            var end = Min(resetsAt, Min(cursor.AddHours(1),
                windowStart + TimeSpan.FromTicks(AdaptiveWeeklyUsageStore.BucketDuration.Ticks * (index + 1))));
            var horizon = (cursor - latest.RecordedAt + (end - cursor) / 2).TotalMinutes;
            var historyWeight = 1 - Math.Exp(-horizon * historyInfluence / RecentInfluenceDuration.TotalMinutes);
            var bucketRate = BucketRate(profiles, index, windowStart) ?? historicalRate;
            var rate = currentRatePerMinute is { } currentRate
                ? currentRate * (1 - historyWeight) + bucketRate * historyWeight
                : bucketRate;
            var consumed = rate * (end - cursor).TotalMinutes;
            if (consumed >= remaining && rate > 0)
            {
                var limit = cursor.AddMinutes(remaining / rate);
                points.Add(new(limit, 0));
                return Result(new(limit, 0, limit < resetsAt, points));
            }

            remaining = Math.Max(0, remaining - consumed);
            cursor = end;
            points.Add(new(cursor, remaining));
        }

        return Result(new(resetsAt, remaining, false, points));

        AdaptiveWeeklyForecastProjection Result(UsageTrendForecast forecast) => new(forecast,
            currentRatePerMinute.HasValue
                ? $"Quota forecast: {FormatRecentBasis(recentDuration)} + {profiles.Length} past week{(profiles.Length == 1 ? string.Empty : "s")} ({coverage:P0} history coverage); estimate only."
                : $"Quota forecast: historical usage only · {profiles.Length} past week{(profiles.Length == 1 ? string.Empty : "s")} ({coverage:P0} history coverage); estimate only.",
            UsesHistory: true);
    }

    private static string FormatRecentBasis(TimeSpan? recentDuration) => recentDuration is { } duration
        ? duration.TotalHours < 1 ? $"recent {duration.TotalMinutes:0} min" : $"recent {duration.TotalHours:0.#} h"
        : "current pace";

    private static bool IsRepresentative(AdaptiveWeeklyUsageCycle cycle) =>
        cycle.WindowMinutes == 10080
        && double.IsFinite(cycle.ObservedMinutes) && cycle.ObservedMinutes is >= 5040 and <= 10080
        && double.IsFinite(cycle.ConsumedPercent) && cycle.ConsumedPercent >= 0
        && cycle.Buckets.All(bucket => bucket.Index is >= 0 and < 28
            && double.IsFinite(bucket.ObservedMinutes) && bucket.ObservedMinutes is >= 0 and <= 360
            && double.IsFinite(bucket.ConsumedPercent) && bucket.ConsumedPercent >= 0)
        && cycle.Buckets.Select(bucket => bucket.Index).Distinct().Count() == cycle.Buckets.Length
        && Math.Abs(cycle.Buckets.Sum(bucket => bucket.ObservedMinutes) - cycle.ObservedMinutes) < 0.001
        && Math.Abs(cycle.Buckets.Sum(bucket => bucket.ConsumedPercent) - cycle.ConsumedPercent) < 0.001
        && Coverage(cycle) >= MinimumCoverage
        // Observing only daytime activity cannot establish an around-the-clock baseline.
        && Enumerable.Range(0, 4).All(slot => cycle.Buckets
            .Where(bucket => bucket.Index % 4 == slot).Sum(bucket => bucket.ObservedMinutes) >= 360);

    private static double Coverage(AdaptiveWeeklyUsageCycle cycle) =>
        Math.Min(cycle.ObservedMinutes, cycle.Buckets.Sum(bucket => bucket.ObservedMinutes)) / cycle.WindowMinutes;

    private static double Weight(AdaptiveWeeklyUsageCycle cycle, DateTimeOffset windowStart) =>
        Coverage(cycle) * Math.Pow(0.5, Math.Max(0, (windowStart - cycle.ResetsAt).TotalDays) / HistoryHalfLifeDays);

    private static double? BucketRate(AdaptiveWeeklyUsageCycle[] profiles, int index, DateTimeOffset windowStart)
    {
        var rates = profiles.Where(cycle => HasMatchingCalendarPhase(cycle, windowStart))
            .Select(cycle => (Cycle: cycle, Bucket: cycle.Buckets.FirstOrDefault(bucket => bucket.Index == index)))
            .Where(item => item.Bucket is { ObservedMinutes: >= 180 })
            .Select(item => (Rate: item.Bucket!.ConsumedPercent / item.Bucket.ObservedMinutes,
                Weight: Weight(item.Cycle, windowStart) * item.Bucket.ObservedMinutes / 360))
            .ToArray();
        return rates.Length >= MinimumPatternCycles ? WeightedMedian(rates) : null;
    }

    private static bool HasMatchingCalendarPhase(AdaptiveWeeklyUsageCycle cycle, DateTimeOffset windowStart)
    {
        var previous = cycle.ResetsAt.AddMinutes(-cycle.WindowMinutes).ToLocalTime();
        var current = windowStart.ToLocalTime();
        // Older files store reset-relative buckets. Do not reinterpret shifted resets or DST as the same clock-time pattern.
        return previous.DayOfWeek == current.DayOfWeek && previous.Offset == current.Offset
            && Math.Abs((previous.TimeOfDay - current.TimeOfDay).TotalMinutes) < 1;
    }

    private static int GetBucketIndex(DateTimeOffset instant, DateTimeOffset windowStart) =>
        Math.Clamp((int)((instant - windowStart).TotalHours / 6), 0, 27);

    private static double WeightedMedian(IEnumerable<(double Rate, double Weight)> values)
    {
        var ordered = values.OrderBy(value => value.Rate).ToArray();
        var half = ordered.Sum(value => value.Weight) / 2;
        var cumulative = 0d;
        foreach (var value in ordered)
        {
            cumulative += value.Weight;
            if (cumulative >= half)
            {
                return value.Rate;
            }
        }

        return ordered[^1].Rate;
    }

    internal static UsageTrendForecast ProjectAtCurrentRate(UsageHistoryEntry latest, DateTimeOffset resetsAt, double rate)
    {
        var remainingAtReset = latest.RemainingPercent - rate * (resetsAt - latest.RecordedAt).TotalMinutes;
        if (rate <= 0 || remainingAtReset >= 0)
        {
            return new(resetsAt, Math.Max(0, remainingAtReset), false, [new(resetsAt, Math.Max(0, remainingAtReset))]);
        }

        var limit = latest.RecordedAt.AddMinutes(latest.RemainingPercent / rate);
        return new(limit, 0, true, [new(limit, 0)]);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}
[JsonSerializable(typeof(AdaptiveWeeklyUsageState))]
internal sealed partial class AdaptiveWeeklyUsageJsonContext : JsonSerializerContext
{
}
