using System.Globalization;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class CodexUsageTablePage : ContentPage, IDisposable
{
    private readonly CodexUsageService _service;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private MarkdownContent _content = new(string.Empty);
    private bool _disposed;

    internal CodexUsageTablePage(CodexUsageService service, Func<DateTimeOffset>? clock = null)
    {
        _service = service;
        _clock = clock ?? (() => DateTimeOffset.Now);
        Id = "nl.mathijs.codexusage.table";
        Name = "Open";
        Title = "Codex usage in text";
        Icon = new IconInfo("\uE8A5");
        service.Updated += OnUpdated;
        Refresh();
    }

    public override IContent[] GetContent() { lock (_gate) { return [_content]; } }

    internal static string Format(UsagePresentation view, DateTimeOffset now, TimeSpan interval, TimeZoneInfo? timeZone = null)
    {
        var displayTimeZone = timeZone ?? TimeZoneInfo.Local;
        var snapshot = view.Usage;
        var freshness = snapshot.Source is UsageDataSource.Initializing or UsageDataSource.Unavailable
            ? "Unavailable" : UsageFreshness.Classify(snapshot.UpdatedAt, now, interval, snapshot.Source == UsageDataSource.LastConfirmed).ToString();
        var body = new StringBuilder("# Codex usage in text\n\n")
            .Append("Status: ").Append(freshness).Append(view.IsLoading ? "; refreshing" : string.Empty)
            .Append(". Source: ").Append(snapshot.SourceDisplayName).Append(".\n\n");
        if (snapshot.Source is not (UsageDataSource.Initializing or UsageDataSource.Unavailable))
            body.Append("Observed: ").Append(LocalTime(snapshot.UpdatedAt, displayTimeZone))
                .Append(". Percentages describe that observation.\n\n");
        body.Append("Times are local (").Append(UsageText.EscapeMarkdown(displayTimeZone.Id)).Append(").\n\n");
        if (snapshot.OrdinaryUsageAllowed == false) body.Append("**Ordinary usage was reported blocked.**\n\n");
        foreach (var group in UsageQuotaGroups.Create(snapshot))
        {
            body.Append("## ").Append(UsageText.EscapeMarkdown(group.Name)).Append("\n\n");
            foreach (var item in group.Windows) AppendWindow(body, item.Title, item.Window, now, displayTimeZone);
            body.Append('\n');
        }
        body.Append("\nAvailable earned resets at observation: ").Append(snapshot.ResetCredits?.AvailableCount.ToString(CultureInfo.InvariantCulture) ?? "Not reported")
            .Append(".\n\n## Recent weekly observations\n\nLast 20 measurements; remaining allowance.\n\n");
        foreach (var point in view.WeeklyHistory.TakeLast(20))
            body.Append("- ").Append(LocalTime(point.RecordedAt, displayTimeZone)).Append(": **")
                .Append(point.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture)).Append("%**\n");
        body.Append("\n## Locally observed daily tokens\n\nThese are local activity totals, not account-wide billing or quota percentages.\n\n");
        body.Append("Availability: ").Append(view.TokenUsage.Status).Append(".\n\n");
        // Full-width text rows avoid the host Markdown table's narrow numeric cells.
        foreach (var day in view.TokenUsage.Days.TakeLast(8))
            body.Append("- ").Append(day.Date.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)).Append(": **")
                .Append(day.TotalTokens.ToString("N0", CultureInfo.InvariantCulture)).Append("** tokens\n");
        return body.ToString();
    }

    private static void AppendWindow(StringBuilder body, string label, RateLimitWindow? window, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        body.Append("- **").Append(UsageText.EscapeMarkdown(label)).Append(":** ");
        if (window is null) { body.Append("Not reported\n"); return; }
        var valid = double.IsFinite(window.UsedPercent) && window.UsedPercent is >= 0 and <= 100
            && window.WindowMinutes > 0
            && (long)window.WindowMinutes * TimeSpan.TicksPerMinute <= Math.Min(window.ResetsAt.Ticks, window.ResetsAt.UtcTicks);
        body.Append(valid ? window.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "% remaining" : "Invalid")
            .Append(" · resets ").Append(LocalTime(window.ResetsAt, timeZone));
        if (window.ResetsAt <= now) body.Append(" · Reset passed; refresh required");
        body.Append('\n');
    }

    private static string LocalTime(DateTimeOffset time, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(time, timeZone).ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    private void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var body = Format(_service.GetPresentation(), _clock(), _service.RefreshInterval);
            _content = new MarkdownContent(body);
            Commands = [new CommandContextItem(new RefreshUsageCommand(_service)) { Title = "Refresh usage" },
                new CommandContextItem(new CopyTextCommand(body)) { Title = "Copy usage text" }];
        }
        RaiseItemsChanged(0);
    }
    private void OnUpdated(object? sender, EventArgs args) => Refresh();
    public void Dispose() { lock (_gate) { _disposed = true; _service.Updated -= OnUpdated; } }
}
