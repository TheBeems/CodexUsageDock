using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Globalization;

namespace CodexUsageDock;

internal enum UsageDockItemKind
{
    FiveHour,
    Weekly,
    ResetsAndCredits,
}

internal sealed partial class UsageDockItem : UsageDockListItem, IDisposable
{
    private readonly CodexUsageService _usage;
    private readonly UsageDockItemKind _kind;
    private readonly CodexUsageDockSettingsPage? _settings;

    public UsageDockItem(CodexUsageService usage, UsageDockItemKind kind, CodexUsageDockPage details, CodexUsageDockSettingsPage? settings = null)
        : base(details)
    {
        _usage = usage;
        _kind = kind;
        _settings = settings;
        _usage.Updated += OnUpdated;
        UpdateText();
    }

    private void OnUpdated(object? sender, EventArgs e) => UpdateText();

    private void UpdateText()
    {
        var snapshot = _usage.Current;
        var now = DateTimeOffset.Now;
        var window = _kind == UsageDockItemKind.FiveHour ? snapshot.Primary : snapshot.Secondary;
        if (snapshot.Source == UsageDataSource.Unavailable)
        {
            (Title, Subtitle) = FormatUnavailable(_kind, _settings?.CompactDock == true);
            Icon = new IconInfo("\uE783");
            return;
        }

        if (_kind == UsageDockItemKind.ResetsAndCredits)
        {
            Title = FormatResetsAndCredits(snapshot);
            Subtitle = CombineStatusAndDetail(
                FormatSourceFreshness(snapshot, now, _usage.RefreshInterval),
                _settings?.CompactDock == true ? string.Empty : FormatResetExpiry(snapshot.ResetCredits, now));
            Icon = new IconInfo("\uE777");
            return;
        }

        var compact = _settings?.CompactDock == true;
        var label = _kind == UsageDockItemKind.FiveHour ? "5h" : compact ? "W" : "Week";
        if (window is null)
        {
            var dataWasLoaded = snapshot.Primary is not null || snapshot.Secondary is not null;
            Title = _kind == UsageDockItemKind.FiveHour && dataWasLoaded ? "5h inactive" : $"{label} --";
            Subtitle = _kind == UsageDockItemKind.FiveHour && dataWasLoaded
                ? "No five-hour limit currently active"
                : FormatSourceFreshness(snapshot, now, _usage.RefreshInterval);
            Icon = new IconInfo("\uE783");
            return;
        }

        if (!UsageFreshness.IsValidWindow(window, now))
        {
            Title = $"{label} --";
            Subtitle = CombineStatusAndDetail(
                FormatSourceFreshness(snapshot, now, _usage.RefreshInterval),
                "Window data expired or invalid");
            Icon = new IconInfo("\uE783");
            return;
        }

        Title = FormatQuotaTitle(_kind, window.RemainingPercent, compact);
        var reset = compact || _settings?.ShowResetTime == false ? string.Empty : $"reset {FormatReset(window.ResetsAt)}";
        Subtitle = CombineStatusAndDetail(FormatSourceFreshness(snapshot, now, _usage.RefreshInterval), reset);
        Icon = new IconInfo(window.RemainingPercent <= 10 ? "\uE7BA" : "\uE916");
    }

    internal void Refresh() => UpdateText();

    internal static string FormatFallbackAge(CodexUsageSnapshot snapshot, DateTimeOffset now)
    {
        TimeSpan age;
        try
        {
            age = now - snapshot.UpdatedAt;
        }
        catch (ArgumentOutOfRangeException)
        {
            return "Fallback · age unavailable";
        }

        if (age < TimeSpan.Zero)
        {
            return "Fallback · timestamp is in the future";
        }

        return age < TimeSpan.FromMinutes(1) ? "Fallback · less than a minute old"
            : age < TimeSpan.FromHours(1) ? $"Fallback · {(int)age.TotalMinutes} minutes old"
            : age < TimeSpan.FromDays(1) ? $"Fallback · {(int)age.TotalHours} hours old"
            : $"Fallback · {(int)age.TotalDays} days old";
    }

    internal static string FormatQuotaTitle(UsageDockItemKind kind, double remainingPercent, bool compact)
    {
        var label = kind switch
        {
            UsageDockItemKind.FiveHour => "5h",
            UsageDockItemKind.Weekly => compact ? "W" : "Week",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The resets and credits item has no quota percentage."),
        };

        return compact
            ? $"{label}{remainingPercent:0}%"
            : $"{label} {remainingPercent:0}%";
    }

    internal static string FormatSourceFreshness(
        CodexUsageSnapshot snapshot,
        DateTimeOffset now,
        TimeSpan? refreshInterval = null)
    {
        if (snapshot.Source == UsageDataSource.Unavailable)
        {
            return "Codex usage unavailable";
        }

        if (snapshot.Source == UsageDataSource.Initializing)
        {
            return "Waiting for Codex";
        }

        if (snapshot.Source == UsageDataSource.LastConfirmed)
        {
            return FormatConfirmedAge(snapshot, now);
        }

        var state = UsageFreshness.Classify(
            snapshot.UpdatedAt,
            now,
            refreshInterval ?? TimeSpan.FromMinutes(1));
        return state switch
        {
            UsageFreshnessState.Fresh when snapshot.Source == UsageDataSource.LocalSession => FormatFallbackAge(snapshot, now),
            UsageFreshnessState.Fresh => $"Live · just updated at {FormatLocalTime(snapshot.UpdatedAt)}",
            UsageFreshnessState.Stale => $"Stale · updated {FormatAge(snapshot.UpdatedAt, now)} ago",
            UsageFreshnessState.Future => $"Timestamp is in the future ({FormatLocalTime(snapshot.UpdatedAt)})",
            _ => "Usage age unavailable",
        };
    }

    private static string FormatConfirmedAge(CodexUsageSnapshot snapshot, DateTimeOffset now)
    {
        var age = FormatAge(snapshot.UpdatedAt, now);
        return age == "in the future"
            ? "Last confirmed · timestamp is in the future"
            : $"Last confirmed · {age} old";
    }

    private static string FormatAge(DateTimeOffset timestamp, DateTimeOffset now)
    {
        TimeSpan age;
        try
        {
            age = now - timestamp;
        }
        catch (ArgumentOutOfRangeException)
        {
            return "age unavailable";
        }

        if (age < TimeSpan.Zero)
        {
            return "in the future";
        }

        return age < TimeSpan.FromMinutes(1)
            ? "less than a minute"
            : age < TimeSpan.FromHours(1)
                ? $"{(int)age.TotalMinutes} minutes"
                : age < TimeSpan.FromDays(1)
                    ? $"{(int)age.TotalHours} hours"
                    : $"{(int)age.TotalDays} days";
    }

    private static string FormatLocalTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    private static string CombineStatusAndDetail(string status, string detail) =>
        detail.Length == 0 ? status : $"{status} · {detail}";

    internal static string FormatResetsAndCredits(CodexUsageSnapshot snapshot)
    {
        var resets = snapshot.ResetCredits is null ? "--" : snapshot.ResetCredits.AvailableCount.ToString(CultureInfo.CurrentCulture);
        var title = $"{resets} resets";
        if (snapshot.Credits is { Unlimited: true })
        {
            return $"{title} · Unlimited";
        }

        if (snapshot.Credits is { HasCredits: true, Balance: { Length: > 0 } balance })
        {
            return UsageText.SanitizeExternal(balance, 64) is { } safeBalance
                ? $"{title} · {safeBalance}"
                : title;
        }

        return title;
    }

    internal static string FormatResetExpiry(RateLimitResetCredits? resets, DateTimeOffset now)
    {
        var nextExpiry = resets?.Credits?
            .Where(credit => credit.ExpiresAt > now)
            .MinBy(credit => credit.ExpiresAt)?
            .ExpiresAt;

        if (nextExpiry is not { } expiry)
        {
            return "expiration unavailable";
        }

        var remaining = expiry - now;
        return remaining < TimeSpan.FromHours(24)
            ? $"expires in {(int)Math.Ceiling(remaining.TotalHours)} hours"
            : $"expires in {(int)Math.Ceiling(remaining.TotalDays)} days";
    }

    internal static (string Title, string Subtitle) FormatUnavailable(UsageDockItemKind kind, bool compact = false) =>
        (kind switch
        {
            UsageDockItemKind.FiveHour => "5h --",
            UsageDockItemKind.Weekly => compact ? "W --" : "Week --",
            _ => "-- resets",
        }, "Codex usage unavailable");

    private static string FormatReset(DateTimeOffset reset)
    {
        var local = reset.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
    }

    public void Dispose()
    {
        _usage.Updated -= OnUpdated;
        GC.SuppressFinalize(this);
    }
}
