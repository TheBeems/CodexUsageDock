using System.Text.Json;

namespace CodexUsageDock;

internal enum ThreadUsageStatus
{
    Available,
    Partial,
    Unsupported,
    Unavailable,
    AccountMismatch,
}

internal sealed record ThreadUsageGroup(
    string? Model,
    string? ReasoningEffort,
    string? Speed,
    long? EstimatedUsageCreditsMicros,
    long? NetNewInputTokens,
    long? CachedInputTokens,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens);

internal sealed record ThreadUsageSnapshot(
    string? ThreadId,
    string? AccountKey,
    DateTimeOffset UpdatedAt,
    ThreadUsageStatus Status,
    long? EstimatedUsageCreditsMicros = null,
    long? EstimatedUsageUsdMicros = null,
    IReadOnlyList<ThreadUsageGroup>? Groups = null)
{
    internal static ThreadUsageSnapshot Unavailable { get; } = new(null, null, DateTimeOffset.MinValue, ThreadUsageStatus.Unavailable);
}

internal static class ThreadUsageParser
{
    internal const int MaximumGroups = 64;

    internal static bool IsValidThreadId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    internal static ThreadUsageSnapshot Parse(JsonElement result, string expectedThreadId, string accountKey, DateTimeOffset now)
    {
        if (!IsValidThreadId(expectedThreadId) || string.IsNullOrWhiteSpace(accountKey) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("threadUsage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("threadId", out var thread) || thread.ValueKind != JsonValueKind.String
            || !string.Equals(thread.GetString(), expectedThreadId, StringComparison.Ordinal))
        {
            return ThreadUsageSnapshot.Unavailable with { UpdatedAt = now };
        }

        var partial = false;
        var credits = ReadNonnegative(usage, "estimatedUsageCreditsMicros", ref partial);
        var usd = ReadNonnegative(usage, "estimatedUsageUsdMicros", ref partial);
        var groups = new List<ThreadUsageGroup>();
        if (usage.TryGetProperty("groups", out var groupValues) && groupValues.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var group in groupValues.EnumerateArray())
            {
                if (++count > MaximumGroups)
                {
                    partial = true;
                    break;
                }
                if (group.ValueKind != JsonValueKind.Object)
                {
                    partial = true;
                    continue;
                }

                var parsed = new ThreadUsageGroup(
                    ReadLabel(group, "model", 80, ref partial),
                    ReadLabel(group, "reasoningEffort", 32, ref partial),
                    ReadLabel(group, "speed", 32, ref partial),
                    ReadNonnegative(group, "estimatedUsageCreditsMicros", ref partial),
                    ReadNonnegative(group, "netNewInputTokens", ref partial),
                    ReadNonnegative(group, "cachedInputTokens", ref partial),
                    ReadNonnegative(group, "inputTokens", ref partial),
                    ReadNonnegative(group, "outputTokens", ref partial),
                    ReadNonnegative(group, "totalTokens", ref partial));
                if (parsed is { Model: null, ReasoningEffort: null, Speed: null, EstimatedUsageCreditsMicros: null,
                    NetNewInputTokens: null, CachedInputTokens: null, InputTokens: null, OutputTokens: null, TotalTokens: null })
                {
                    partial = true;
                    continue;
                }

                groups.Add(parsed);
            }
        }
        else
        {
            partial = true;
        }

        if (credits is null && usd is null && groups.Count == 0)
        {
            return ThreadUsageSnapshot.Unavailable with { UpdatedAt = now };
        }

        return new(expectedThreadId, accountKey, now, partial ? ThreadUsageStatus.Partial : ThreadUsageStatus.Available,
            credits, usd, groups.ToArray());
    }

    private static string? ReadLabel(JsonElement element, string name, int maximumLength, ref bool partial)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var original = value.GetString();
            var safe = UsageText.SanitizeExternal(original, maximumLength);
            if (!string.Equals(original, safe, StringComparison.Ordinal)) partial = true;
            return safe;
        }

        partial = true;
        return null;
    }

    private static long? ReadNonnegative(JsonElement element, string name, ref bool partial)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0) return number;
        partial = true;
        return null;
    }
}
