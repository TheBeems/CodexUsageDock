using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal enum UsageDockItemKind
{
    FiveHour,
    Weekly,
    Status,
}

internal sealed partial class UsageDockItem : ListItem, IDisposable
{
    private readonly CodexUsageService _usage;
    private readonly UsageDockItemKind _kind;

    public UsageDockItem(CodexUsageService usage, UsageDockItemKind kind, CodexUsageDockPage details)
        : base(details)
    {
        _usage = usage;
        _kind = kind;
        _usage.Updated += OnUpdated;
        UpdateText();
    }

    private void OnUpdated(object? sender, EventArgs e) => UpdateText();

    private void UpdateText()
    {
        var snapshot = _usage.Current;
        var window = _kind == UsageDockItemKind.FiveHour ? snapshot.Primary : snapshot.Secondary;
        if (_kind == UsageDockItemKind.Status)
        {
            Title = snapshot.ActiveThreads is > 0 ? $"Codex ● {snapshot.ActiveThreads}" : "Codex ✓";
            Subtitle = $"{snapshot.PlanType ?? "account"} · {snapshot.UpdatedAt.ToLocalTime():HH:mm}";
            Icon = new IconInfo("\uE943");
            return;
        }

        var label = _kind == UsageDockItemKind.FiveHour ? "5u" : "Week";
        if (window is null)
        {
            Title = $"{label} --";
            Subtitle = snapshot.Error ?? "Wachten op Codex";
            Icon = new IconInfo("\uE783");
            return;
        }

        Title = $"{label} {window.RemainingPercent:0}%";
        Subtitle = $"reset {FormatReset(window.ResetsAt)}";
        Icon = new IconInfo(window.RemainingPercent <= 10 ? "\uE7BA" : "\uE916");
    }

    private static string FormatReset(DateTimeOffset reset)
    {
        var local = reset.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("ddd HH:mm");
    }

    public void Dispose()
    {
        _usage.Updated -= OnUpdated;
        GC.SuppressFinalize(this);
    }
}
