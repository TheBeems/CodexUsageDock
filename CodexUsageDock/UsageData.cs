using System.Text;
using System.Text.Json;

namespace CodexUsageDock;

internal static class UsageText
{
    internal static string? SanitizeExternal(string? value, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(value) || maxLength <= 0)
        {
            return null;
        }

        var sanitized = new StringBuilder(Math.Min(value.Length, maxLength));
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = sanitized.Length > 0;
                continue;
            }

            if (pendingSpace && sanitized.Length < maxLength)
            {
                sanitized.Append(' ');
            }

            pendingSpace = false;
            if (sanitized.Length >= maxLength)
            {
                break;
            }

            sanitized.Append(character);
        }

        var result = sanitized.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    internal static string EscapeMarkdown(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '<' or '>' or '#' or '|' or '!')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}

internal sealed record RateLimitWindow(double UsedPercent, int WindowMinutes, DateTimeOffset ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal readonly record struct ClassifiedRateLimitWindows(RateLimitWindow? FiveHour, RateLimitWindow? Weekly);

internal sealed record CreditBalance(bool HasCredits, bool Unlimited, string? Balance);

internal sealed record RateLimitResetCredit(string? Title, string? Status, DateTimeOffset? ExpiresAt);

internal sealed record RateLimitResetCredits(int AvailableCount, IReadOnlyList<RateLimitResetCredit>? Credits);

internal sealed record UsageHistoryEntry(DateTimeOffset RecordedAt, double RemainingPercent);

internal sealed record UsageTrendForecastPoint(DateTimeOffset RecordedAt, double RemainingPercent);

internal sealed record UsageTrendForecast(
    DateTimeOffset EndsAt,
    double RemainingPercent,
    bool ReachesLimitBeforeReset,
    IReadOnlyList<UsageTrendForecastPoint>? Points = null);

internal enum UsageDataSource
{
    Initializing,
    AppServer,
    LocalSession,
    Unavailable,
}

internal sealed record CodexUsageSnapshot(
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    string? PlanType,
    CreditBalance? Credits,
    RateLimitResetCredits? ResetCredits,
    DateTimeOffset UpdatedAt,
    UsageDataSource Source,
    string? Error)
{
    public string SourceDisplayName => Source switch
    {
        UsageDataSource.AppServer => "standalone Codex CLI app-server",
        UsageDataSource.LocalSession => "local Codex session metadata (desktop app, CLI, or another client)",
        UsageDataSource.Unavailable => "not available",
        _ => "initializing",
    };

    public static CodexUsageSnapshot Loading { get; } = new(
        null,
        null,
        null,
        null,
        null,
        DateTimeOffset.Now,
        UsageDataSource.Initializing,
        "Waiting for Codex");
}

internal static class RateLimitWindowParser
{
    internal static ClassifiedRateLimitWindows Classify(params RateLimitWindow?[] windows)
    {
        RateLimitWindow? fiveHour = null;
        RateLimitWindow? weekly = null;
        foreach (var window in windows)
        {
            if (window is null)
            {
                continue;
            }

            if (window.WindowMinutes == (int)TimeSpan.FromHours(5).TotalMinutes)
            {
                fiveHour = window;
            }
            else if (window.WindowMinutes == (int)TimeSpan.FromDays(7).TotalMinutes)
            {
                weekly = window;
            }
        }

        return new ClassifiedRateLimitWindows(fiveHour, weekly);
    }

    internal static void ThrowIfNoKnownWindow(ClassifiedRateLimitWindows windows)
    {
        if (windows.FiveHour is null && windows.Weekly is null)
        {
            throw new InvalidOperationException("Codex app-server did not return recognized rate-limit windows.");
        }
    }

    internal static RateLimitWindow? TryParse(
        JsonElement limits,
        string propertyName,
        string usedPercentPropertyName,
        string durationPropertyName,
        string resetsAtPropertyName)
    {
        if (limits.ValueKind != JsonValueKind.Object
            || !limits.TryGetProperty(propertyName, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty(usedPercentPropertyName, out var usedPercentValue)
            || usedPercentValue.ValueKind != JsonValueKind.Number
            || !usedPercentValue.TryGetDouble(out var usedPercent)
            || !window.TryGetProperty(durationPropertyName, out var durationValue)
            || durationValue.ValueKind != JsonValueKind.Number
            || !durationValue.TryGetInt32(out var duration)
            || !window.TryGetProperty(resetsAtPropertyName, out var resetsAtValue)
            || resetsAtValue.ValueKind != JsonValueKind.Number
            || !resetsAtValue.TryGetInt64(out var resetsAt))
        {
            return null;
        }

        try
        {
            return new RateLimitWindow(usedPercent, duration, DateTimeOffset.FromUnixTimeSeconds(resetsAt));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
