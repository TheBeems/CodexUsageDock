namespace CodexUsageDock;

internal sealed record AllowanceRestoration(
    DateTimeOffset DetectedAt,
    double PreviousRemainingPercent,
    double CurrentRemainingPercent)
{
    internal double IncreasePercent => CurrentRemainingPercent - PreviousRemainingPercent;
}

internal static class WeeklyAllowanceRestoration
{
    internal static AllowanceRestoration[] Detect(
        IReadOnlyList<UsageHistoryEntry> history,
        RateLimitWindow window,
        DateTimeOffset now)
    {
        if (window.WindowMinutes <= 0)
        {
            return [];
        }

        var windowStart = window.ResetsAt - TimeSpan.FromMinutes(window.WindowMinutes);
        var samples = history
            .Where(IsValid)
            .OrderBy(sample => sample.RecordedAt)
            .GroupBy(sample => sample.RecordedAt)
            .Select(group => group.Last())
            .ToArray();
        var restorations = new List<AllowanceRestoration>();

        for (var index = 1; index < samples.Length; index++)
        {
            var previous = samples[index - 1];
            var current = samples[index];
            if (!IsIncrease(previous, current)
                || current.RecordedAt < windowStart
                || current.RecordedAt > now
                || current.RecordedAt > window.ResetsAt)
            {
                continue;
            }

            var isKnownWindowTransition = previous.ResetsAt.HasValue
                && current.ResetsAt.HasValue
                && previous.ResetsAt.Value != current.ResetsAt.Value;
            var isScheduledRollover = isKnownWindowTransition
                && current.RecordedAt >= previous.ResetsAt!.Value;
            var isLegacyCrossBoundary = !isKnownWindowTransition
                && previous.RecordedAt < windowStart;
            if (isScheduledRollover || isLegacyCrossBoundary)
            {
                continue;
            }

            restorations.Add(new AllowanceRestoration(
                current.RecordedAt,
                previous.RemainingPercent,
                current.RemainingPercent));
        }

        return [.. restorations];
    }

    internal static bool IsIncrease(UsageHistoryEntry previous, UsageHistoryEntry current) =>
        current.RemainingPercent > previous.RemainingPercent;

    private static bool IsValid(UsageHistoryEntry sample) =>
        double.IsFinite(sample.RemainingPercent)
        && sample.RemainingPercent is >= 0 and <= 100;
}
