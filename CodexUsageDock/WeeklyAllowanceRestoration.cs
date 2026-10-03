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
    internal static readonly TimeSpan ResetCycleTolerance = TimeSpan.FromMinutes(1);

    internal static AllowanceRestoration[] Detect(
        IReadOnlyList<UsageHistoryEntry> history,
        RateLimitWindow window,
        DateTimeOffset now,
        DateTimeOffset? retainedFrom = null)
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
                || current.RecordedAt < (retainedFrom ?? windowStart)
                || current.RecordedAt > now
                || current.RecordedAt > window.ResetsAt)
            {
                continue;
            }

            var isKnownWindowTransition = IsCycleChange(previous, current);
            var isScheduledRollover = isKnownWindowTransition
                && current.RecordedAt >= previous.ResetsAt!.Value;
            var isLegacyCrossBoundary = !isKnownWindowTransition
                && previous.RecordedAt < windowStart
                && current.RecordedAt >= windowStart;
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

    internal static bool IsCycleChange(UsageHistoryEntry previous, UsageHistoryEntry current) =>
        previous.ResetsAt.HasValue
        && current.ResetsAt.HasValue
        && (current.ResetsAt.Value - previous.ResetsAt.Value).Duration() > ResetCycleTolerance
        && !IsUnusedResetEstimate(previous, current);

    private static bool IsUnusedResetEstimate(UsageHistoryEntry previous, UsageHistoryEntry current)
    {
        if (previous.RemainingPercent != 100
            || previous.WindowMinutes is not > 0
            || current.WindowMinutes != previous.WindowMinutes)
        {
            return false;
        }

        // Before first consumption, the server can report a deadline one full window from each reading.
        // The deadline may settle between readings when usage starts; neither change proves a reset.
        var duration = TimeSpan.FromMinutes(previous.WindowMinutes.Value);
        return (previous.ResetsAt!.Value - previous.RecordedAt - duration).Duration() <= ResetCycleTolerance
            && current.ResetsAt!.Value - previous.RecordedAt - duration >= -ResetCycleTolerance
            && current.ResetsAt.Value - current.RecordedAt - duration <= ResetCycleTolerance;
    }

    internal static bool IsInCycle(UsageHistoryEntry sample, DateTimeOffset resetsAt, int windowMinutes) =>
        (!sample.WindowMinutes.HasValue || sample.WindowMinutes.Value == windowMinutes)
        && (!sample.ResetsAt.HasValue || (sample.ResetsAt.Value - resetsAt).Duration() <= ResetCycleTolerance);

    internal static bool IsDiscontinuity(UsageHistoryEntry previous, UsageHistoryEntry current) =>
        IsIncrease(previous, current) || IsCycleChange(previous, current);

    private static bool IsValid(UsageHistoryEntry sample) =>
        double.IsFinite(sample.RemainingPercent)
        && sample.RemainingPercent is >= 0 and <= 100;
}
