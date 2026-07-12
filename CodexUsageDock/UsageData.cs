using System.Text.Json;

namespace CodexUsageDock;

internal sealed record RateLimitWindow(double UsedPercent, int WindowMinutes, DateTimeOffset ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal readonly record struct ClassifiedRateLimitWindows(RateLimitWindow? FiveHour, RateLimitWindow? Weekly);

internal sealed record CreditBalance(bool HasCredits, bool Unlimited, string? Balance);

internal sealed record RateLimitResetCredit(string? Title, string? Status, DateTimeOffset? ExpiresAt);

internal sealed record RateLimitResetCredits(int AvailableCount, IReadOnlyList<RateLimitResetCredit>? Credits);

internal sealed record UsageHistoryEntry(DateTimeOffset RecordedAt, double RemainingPercent);

internal enum UsageDataSource
{
    Initializing,
    AppServer,
    LocalSession,
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
        UsageDataSource.AppServer => "Codex app-server",
        UsageDataSource.LocalSession => "local Codex session",
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
