namespace CodexUsageDock;

internal enum UsageFreshnessState
{
    Unknown,
    Fresh,
    Stale,
    Future,
    LastConfirmed,
}

internal static class UsageFreshness
{
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(5);

    internal static TimeSpan MaximumAge(TimeSpan refreshInterval) =>
        refreshInterval > MinimumAge ? refreshInterval : MinimumAge;

    internal static UsageFreshnessState Classify(
        DateTimeOffset? updatedAt,
        DateTimeOffset now,
        TimeSpan refreshInterval,
        bool isLastConfirmed = false)
    {
        if (isLastConfirmed)
        {
            return UsageFreshnessState.LastConfirmed;
        }

        if (updatedAt is not { } timestamp)
        {
            return UsageFreshnessState.Unknown;
        }

        try
        {
            var age = now - timestamp;
            if (age < TimeSpan.Zero)
            {
                return UsageFreshnessState.Future;
            }

            return age <= MaximumAge(refreshInterval)
                ? UsageFreshnessState.Fresh
                : UsageFreshnessState.Stale;
        }
        catch (ArgumentOutOfRangeException)
        {
            return UsageFreshnessState.Unknown;
        }
    }

    internal static bool IsFresh(
        DateTimeOffset? updatedAt,
        DateTimeOffset now,
        TimeSpan refreshInterval,
        bool isLastConfirmed = false) =>
        Classify(updatedAt, now, refreshInterval, isLastConfirmed) == UsageFreshnessState.Fresh;

    internal static bool IsValidWindow(RateLimitWindow? window, DateTimeOffset now) =>
        window is { WindowMinutes: > 0 } candidate
        && double.IsFinite(candidate.UsedPercent)
        && candidate.UsedPercent is >= 0 and <= 100
        && candidate.ResetsAt > now;
}
