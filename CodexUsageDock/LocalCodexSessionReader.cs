using System.Text.Json;

namespace CodexUsageDock;

internal static class LocalCodexSessionReader
{
    internal static CodexUsageSnapshot ReadLatest(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        var files = Directory.EnumerateFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(path =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new FileInfo(path);
            })
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(12);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RateLimitWindow? primary = null;
            RateLimitWindow? secondary = null;
            string? plan = null;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (!document.RootElement.TryGetProperty("payload", out var payload)
                        || payload.ValueKind != JsonValueKind.Object
                        || !payload.TryGetProperty("rate_limits", out var rateLimits)
                        || rateLimits.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    primary = RateLimitWindowParser.TryParse(rateLimits, "primary", "used_percent", "window_minutes", "resets_at") ?? primary;
                    secondary = RateLimitWindowParser.TryParse(rateLimits, "secondary", "used_percent", "window_minutes", "resets_at") ?? secondary;
                    if (rateLimits.TryGetProperty("plan_type", out var planType) && planType.ValueKind == JsonValueKind.String)
                    {
                        plan = UsageText.SanitizeExternal(planType.GetString(), 32) ?? plan;
                    }
                }
                catch (JsonException)
                {
                }
            }

            if (primary is not null || secondary is not null)
            {
                var windows = RateLimitWindowParser.Classify(primary, secondary);
                return new CodexUsageSnapshot(windows.FiveHour, windows.Weekly, plan, null, null, file.LastWriteTime, UsageDataSource.LocalSession, null);
            }
        }

        throw new InvalidOperationException("No recent Codex usage data was found.");
    }
}
