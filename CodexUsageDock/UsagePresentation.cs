namespace CodexUsageDock;

internal sealed record UsagePresentation(
    CodexUsageSnapshot Usage,
    IReadOnlyList<UsageHistoryEntry> PrimaryHistory,
    IReadOnlyList<UsageHistoryEntry> WeeklyHistory,
    AdaptiveWeeklyUsageHistory AdaptiveWeeklyHistory,
    LocalTokenUsageSnapshot TokenUsage,
    bool IsLoading);
