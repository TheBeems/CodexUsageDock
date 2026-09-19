namespace CodexUsageDock;

internal sealed record UsageQuotaWindow(string Title, RateLimitWindow? Window);
internal sealed record UsageQuotaGroup(string Name, bool IsPrimary, IReadOnlyList<UsageQuotaWindow> Windows);

// Share display names and deduplication between the dashboard and its text alternative.
internal static class UsageQuotaGroups
{
    internal static IReadOnlyList<UsageQuotaGroup> Create(CodexUsageSnapshot snapshot)
    {
        var defaults = new List<UsageQuotaWindow>
        {
            CreateWindow(snapshot.Primary, "5-hour"),
            CreateWindow(snapshot.Secondary, "Weekly"),
        };
        var groups = new List<UsageQuotaGroup> { new("Codex", true, defaults) };
        foreach (var bucket in snapshot.Buckets?.Take(32) ?? [])
        {
            var windows = new[] { bucket.Primary, bucket.Secondary }.OfType<RateLimitWindow>();
            if (string.Equals(bucket.Id, snapshot.DefaultBucketId, StringComparison.Ordinal))
            {
                foreach (var window in windows)
                {
                    // API primary/secondary slots need not match the classified five-hour/weekly slots.
                    if (!defaults.Any(item => item.Window == window)) defaults.Add(CreateWindow(window));
                }
                continue;
            }

            var name = UsageText.SanitizeExternal(bucket.Name, 70)
                ?? UsageText.SanitizeExternal(bucket.Id, 70) ?? "Additional quota";
            var additional = windows.Select(window => CreateWindow(window)).ToArray();
            groups.Add(new(name, false, additional.Length > 0 ? additional : [CreateWindow(null)]));
        }
        return groups;
    }

    private static UsageQuotaWindow CreateWindow(RateLimitWindow? window, string fallback = "Quota") =>
        new(window is { WindowMinutes: > 0 } ? FormatDuration(window.WindowMinutes) : fallback, window);

    private static string FormatDuration(int minutes) => minutes switch
    {
        300 => "5-hour",
        10080 => "Weekly",
        > 0 when minutes % 1440 == 0 => $"{minutes / 1440}-day",
        > 0 when minutes % 60 == 0 => $"{minutes / 60}-hour",
        _ => $"{minutes}-minute",
    };
}
