namespace CodexUsageDock;

internal static class UsageTrendHistory
{
    internal static TimeSpan Freshness(TimeSpan refreshInterval) =>
        UsageFreshness.MaximumAge(refreshInterval);

    internal static TimeSpan MaximumGap(TimeSpan refreshInterval) => TimeSpan.FromTicks(Freshness(refreshInterval).Ticks * 3);

    internal static UsageHistoryEntry[] LatestSegment(
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset? windowStart,
        DateTimeOffset? windowEnd,
        DateTimeOffset now,
        TimeSpan maximumGap)
    {
        var samples = history
            .Where(sample => sample is not null && double.IsFinite(sample.RemainingPercent)
                && sample.RemainingPercent is >= 0 and <= 100
                && (!windowStart.HasValue || sample.RecordedAt >= windowStart)
                && (!windowEnd.HasValue || sample.RecordedAt <= windowEnd)
                && sample.RecordedAt <= now)
            .OrderBy(sample => sample.RecordedAt)
            .GroupBy(sample => sample.RecordedAt)
            .Select(group => group.Last())
            .ToArray();
        var start = 0;
        for (var index = 1; index < samples.Length; index++)
        {
            if (samples[index].RecordedAt - samples[index - 1].RecordedAt > maximumGap
                || WeeklyAllowanceRestoration.IsIncrease(samples[index - 1], samples[index]))
            {
                start = index;
            }
        }

        return samples[start..];
    }
}
