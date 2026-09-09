using System.Globalization;
using System.Text.Json;

namespace CodexUsageDock;

internal enum AccountUsageStatus
{
    Available,
    Partial,
    Unsupported,
    Unavailable,
}

internal sealed record AccountUsageSnapshot(
    string? AccountKey,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DailyTokenUsage> Days,
    AccountUsageStatus Status,
    long? LifetimeTokens = null,
    long? PeakDailyTokens = null,
    long? LongestRunningTurnSeconds = null,
    long? CurrentStreakDays = null,
    long? LongestStreakDays = null)
{
    internal static AccountUsageSnapshot Unavailable { get; } = new(null, DateTimeOffset.MinValue, [], AccountUsageStatus.Unavailable);
}

internal static class AccountUsageParser
{
    internal const int MaximumDays = 366;
    private const int MaximumInputDays = 4096;

    internal static AccountUsageSnapshot Parse(JsonElement result, string accountKey, DateTimeOffset now)
    {
        if (result.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(accountKey))
        {
            return AccountUsageSnapshot.Unavailable with { UpdatedAt = now };
        }

        var partial = false;
        var daysAvailable = false;
        var days = new SortedDictionary<DateOnly, long>();
        if (result.TryGetProperty("dailyUsageBuckets", out var daily))
        {
            if (daily.ValueKind == JsonValueKind.Array)
            {
                daysAvailable = true;
                var inputCount = 0;
                foreach (var entry in daily.EnumerateArray())
                {
                    if (++inputCount > MaximumInputDays)
                    {
                        partial = true;
                        break;
                    }

                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("startDate", out var dateValue)
                        || dateValue.ValueKind != JsonValueKind.String
                        || !DateOnly.TryParseExact(dateValue.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        || !entry.TryGetProperty("tokens", out var tokensValue)
                        || tokensValue.ValueKind != JsonValueKind.Number
                        || !tokensValue.TryGetInt64(out var tokens)
                        || tokens < 0
                        || !days.TryAdd(date, tokens))
                    {
                        partial = true;
                        continue;
                    }

                    if (days.Count > MaximumDays)
                    {
                        days.Remove(days.First().Key);
                        partial = true;
                    }
                }
            }
            else if (daily.ValueKind != JsonValueKind.Null)
            {
                partial = true;
            }
        }

        var summary = default(JsonElement);
        if (result.TryGetProperty("summary", out var summaryValue))
        {
            if (summaryValue.ValueKind == JsonValueKind.Object)
            {
                summary = summaryValue;
            }
            else if (summaryValue.ValueKind != JsonValueKind.Null)
            {
                partial = true;
            }
        }

        var lifetime = ReadNonnegative(summary, "lifetimeTokens", ref partial);
        var peak = ReadNonnegative(summary, "peakDailyTokens", ref partial);
        var longest = ReadNonnegative(summary, "longestRunningTurnSec", ref partial);
        var currentStreak = ReadNonnegative(summary, "currentStreakDays", ref partial);
        var longestStreak = ReadNonnegative(summary, "longestStreakDays", ref partial);
        if (!daysAvailable && lifetime is null && peak is null && longest is null && currentStreak is null && longestStreak is null)
        {
            return AccountUsageSnapshot.Unavailable with { UpdatedAt = now };
        }

        return new AccountUsageSnapshot(accountKey, now,
            days.Select(day => new DailyTokenUsage(day.Key, day.Value)).ToArray(),
            partial || !daysAvailable ? AccountUsageStatus.Partial : AccountUsageStatus.Available,
            lifetime, peak, longest, currentStreak, longestStreak);
    }

    private static long? ReadNonnegative(JsonElement summary, string name, ref bool partial)
    {
        if (summary.ValueKind != JsonValueKind.Object || !summary.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0)
        {
            return number;
        }

        partial = true;
        return null;
    }
}
