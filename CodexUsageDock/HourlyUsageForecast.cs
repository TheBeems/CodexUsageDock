namespace CodexUsageDock;

internal static class HourlyUsageForecast
{
    internal const int MinimumObservedHours = 12;
    internal const int MinimumObservedDates = 3;

    internal static AdaptiveWeeklyForecastProjection? Project(UsageHistoryEntry latest, DateTimeOffset resetsAt,
        AdaptiveWeeklyUsageHistory history)
    {
        if (history.Hourly is not { } hourly || resetsAt <= latest.RecordedAt
            || latest.RecordedAt < DateTimeOffset.MinValue.AddDays(AdaptiveWeeklyUsageStore.RetentionDays)) return null;
        var cutoff = latest.RecordedAt.AddDays(-AdaptiveWeeklyUsageStore.RetentionDays);
        var samples = new List<(DateTimeOffset At, double Minutes, double Consumed)>();
        if (hourly.RecordedThrough is { } through && through <= latest.RecordedAt)
        {
            foreach (var bucket in hourly.Buckets)
            {
                if (!AdaptiveWeeklyUsageStore.IsValidBucket(bucket) || bucket.StartsAt < cutoff
                    || bucket.StartsAt > through
                    || bucket.ObservedTicks > (through - bucket.StartsAt).Ticks) continue;
                samples.Add((bucket.StartsAt, TimeSpan.FromTicks(bucket.ObservedTicks).TotalMinutes, bucket.ConsumedPercent));
            }
        }

        var legacy = false;
        if (!HasEnoughHistory(samples) && hourly.LegacyRecordedThrough is { } legacyThrough && legacyThrough <= latest.RecordedAt)
        {
            // Preserve the old learned summaries without inventing an hourly distribution or replaying paused raw data.
            samples.Clear();
            foreach (var cycle in history.CompletedCycles.Concat(history.ActiveCycle is { } active ? [active] : []))
            {
                if (cycle.WindowMinutes != 10080 || cycle.ResetsAt < DateTimeOffset.MinValue.AddDays(7)) continue;
                foreach (var bucket in cycle.Buckets)
                {
                    if (bucket.Index is < 0 or >= AdaptiveWeeklyUsageStore.BucketCount
                        || !double.IsFinite(bucket.ObservedMinutes) || bucket.ObservedMinutes is <= 0 or > 360
                        || !double.IsFinite(bucket.ConsumedPercent) || bucket.ConsumedPercent < 0) continue;
                    var start = cycle.ResetsAt.AddDays(-7).AddHours(bucket.Index * 6);
                    var end = start.AddHours(6) < legacyThrough ? start.AddHours(6) : legacyThrough;
                    if (start < cutoff || start >= end || bucket.ObservedMinutes > (end - start).TotalMinutes + 0.000001) continue;
                    samples.Add((start, bucket.ObservedMinutes, bucket.ConsumedPercent));
                }
            }

            legacy = true;
        }

        if (!HasEnoughHistory(samples)) return null;
        var weightedMinutes = 0d;
        var weightedConsumed = 0d;
        foreach (var sample in samples)
        {
            var weight = Math.Pow(0.5, (latest.RecordedAt - sample.At).TotalDays / AdaptiveWeeklyForecast.HistoryHalfLifeDays);
            weightedMinutes += weight * sample.Minutes;
            weightedConsumed += weight * sample.Consumed;
        }

        if (!double.IsFinite(weightedConsumed) || !double.IsFinite(weightedMinutes) || weightedMinutes <= 0) return null;

        var hours = samples.Sum(sample => sample.Minutes) / 60;
        var dates = samples.Select(sample => sample.At.UtcDateTime.Date).Distinct().Count();
        var source = legacy ? "preserved learned history" : "hourly history";
        return new(AdaptiveWeeklyForecast.ProjectAtCurrentRate(latest, resetsAt, weightedConsumed / weightedMinutes),
            $"Quota forecast: historical usage only · {hours:0.#} measured h across {dates} UTC dates ({source}); estimate only.",
            UsesHistory: true);
    }

    private static bool HasEnoughHistory(List<(DateTimeOffset At, double Minutes, double Consumed)> samples) =>
        samples.Sum(sample => sample.Minutes) >= MinimumObservedHours * 60
        && samples.Select(sample => sample.At.UtcDateTime.Date).Distinct().Count() >= MinimumObservedDates;
}
