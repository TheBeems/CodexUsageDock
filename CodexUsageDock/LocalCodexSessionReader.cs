using System.Globalization;
using System.Text.Json;

namespace CodexUsageDock;

internal static class LocalCodexSessionReader
{
    internal static CodexUsageSnapshot ReadLatest(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ReadLatest(LocalStorage.GetCodexHome(), DateTimeOffset.UtcNow, cancellationToken);
    }

    internal static CodexUsageSnapshot ReadLatest(string codexHome, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        CodexUsageSnapshot? latest = null;
        foreach (var directory in new[] { Path.Combine(codexHome, "sessions"), Path.Combine(codexHome, "archived_sessions") })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "rollout-*.jsonl", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var snapshot = ReadFile(file, now, cancellationToken);
                    if (snapshot is not null && (latest is null || snapshot.UpdatedAt > latest.UpdatedAt))
                    {
                        latest = snapshot;
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                LocalStorage.TraceFailure("enumerate fallback sessions", error);
            }
        }

        return latest ?? throw new InvalidOperationException("No usable Codex usage measurement was found.");
    }

    private static CodexUsageSnapshot? ReadFile(string path, DateTimeOffset now, CancellationToken cancellationToken)
    {
        CodexUsageSnapshot? latest = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("timestamp", out var timestamp)
                        || timestamp.ValueKind != JsonValueKind.String
                        || !DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var recordedAt)
                        || recordedAt > now
                        || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                        || !payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object
                        || !TryReadWindow(limits, "primary", out var primary)
                        || !TryReadWindow(limits, "secondary", out var secondary)
                        || (!limits.TryGetProperty("primary", out _) && !limits.TryGetProperty("secondary", out _)))
                    {
                        continue;
                    }

                    var windows = RateLimitWindowParser.Classify(primary, secondary);
                    if ((primary is not null || secondary is not null) && windows.FiveHour is null && windows.Weekly is null)
                    {
                        continue;
                    }

                    var plan = limits.TryGetProperty("plan_type", out var planType) && planType.ValueKind == JsonValueKind.String
                        ? UsageText.SanitizeExternal(planType.GetString(), 32) : null;
                    if (latest is null || recordedAt >= latest.UpdatedAt)
                    {
                        latest = new CodexUsageSnapshot(windows.FiveHour, windows.Weekly, plan, null, null, recordedAt, UsageDataSource.LocalSession, null);
                    }
                }
                catch (JsonException)
                {
                    // A writer may still be appending the final JSONL record.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LocalStorage.TraceFailure("read fallback session", error);
        }

        return latest;
    }

    private static bool TryReadWindow(JsonElement limits, string name, out RateLimitWindow? window)
    {
        window = RateLimitWindowParser.TryParse(limits, name, "used_percent", "window_minutes", "resets_at");
        return window is not null || !limits.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;
    }
}