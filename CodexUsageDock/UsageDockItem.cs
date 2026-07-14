using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Globalization;

namespace CodexUsageDock;

internal enum UsageDockItemKind
{
    FiveHour,
    Weekly,
    ResetsAndCredits,
}

internal sealed partial class UsageDockItem : ListItem, IDisposable
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
        var window = _kind == UsageDockItemKind.FiveHour ? snapshot.Primary : snapshot.Secondary;
        if (snapshot.Source == UsageDataSource.Unavailable)
        {
            (Title, Subtitle) = FormatUnavailable(_kind);
            Icon = new IconInfo("\uE783");
            return;
        }

        if (_kind == UsageDockItemKind.ResetsAndCredits)
        {
            Title = FormatResetsAndCredits(snapshot);
            Subtitle = $"{UsageText.SanitizeExternal(snapshot.PlanType, 32) ?? "account"} · {snapshot.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)}";
            Icon = new IconInfo("\uE777");
            return;
        }

        var label = _kind == UsageDockItemKind.FiveHour ? "5h" : "Week";
        if (window is null)
        {
            var dataWasLoaded = snapshot.Primary is not null || snapshot.Secondary is not null;
            Title = _kind == UsageDockItemKind.FiveHour && dataWasLoaded ? "5h inactive" : $"{label} --";
            Subtitle = _kind == UsageDockItemKind.FiveHour && dataWasLoaded
                ? "No five-hour limit currently active"
                : snapshot.Source == UsageDataSource.Unavailable
                    ? "Codex usage unavailable"
                    : "Waiting for Codex";
            Icon = new IconInfo("\uE783");
            return;
        }

        Title = $"{label} {window.RemainingPercent:0}%";
        Subtitle = _settings?.ShowResetTime == false ? string.Empty : $"reset {FormatReset(window.ResetsAt)}";
        Icon = new IconInfo(window.RemainingPercent <= 10 ? "\uE7BA" : "\uE916");
    }

    internal void Refresh() => UpdateText();

    internal static string FormatResetsAndCredits(CodexUsageSnapshot snapshot)
    {
        var resets = snapshot.ResetCredits is null ? "--" : snapshot.ResetCredits.AvailableCount.ToString(CultureInfo.CurrentCulture);
        var title = $"↻ {resets}";
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

    internal static (string Title, string Subtitle) FormatUnavailable(UsageDockItemKind kind) =>
        (kind switch
        {
            UsageDockItemKind.FiveHour => "5h --",
            UsageDockItemKind.Weekly => "Week --",
            _ => "↻ --",
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
