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

    internal static string Format(UsagePresentation view, DateTimeOffset now, TimeSpan interval)
    {
        var snapshot = view.Usage;
        var freshness = snapshot.Source is UsageDataSource.Initializing or UsageDataSource.Unavailable
            ? "Unavailable" : UsageFreshness.Classify(snapshot.UpdatedAt, now, interval, snapshot.Source == UsageDataSource.LastConfirmed).ToString();
        var body = new StringBuilder("# Codex usage in text\n\nA text alternative to the dashboard charts.\n\n")
            .Append("Status: ").Append(freshness).Append(view.IsLoading ? "; refreshing" : string.Empty)
            .Append(". Source: ").Append(snapshot.SourceDisplayName).Append(".\n\n");
        if (snapshot.Source is not (UsageDataSource.Initializing or UsageDataSource.Unavailable))
            body.Append("Observed UTC: ").Append(Utc(snapshot.UpdatedAt)).Append(". Percentages below describe that observation.\n\n");
        if (snapshot.OrdinaryUsageAllowed == false) body.Append("**Ordinary usage was reported blocked.**\n\n");
        body.Append("| Quota category / window | Remaining at observation | Reset UTC | Window state now |\n| --- | ---: | --- | --- |\n");
        AppendWindow(body, "Default five-hour", snapshot.Primary, now);
        AppendWindow(body, "Default weekly", snapshot.Secondary, now);
        foreach (var bucket in snapshot.Buckets?.Take(32) ?? [])
        {
            var label = UsageText.SanitizeExternal(bucket.Name, 70) ?? UsageText.SanitizeExternal(bucket.Id, 70) ?? "Additional category";
            if (bucket.Id != snapshot.DefaultBucketId || bucket.Primary != snapshot.Primary) AppendWindow(body, label + " primary", bucket.Primary, now);
            if (bucket.Id != snapshot.DefaultBucketId || bucket.Secondary != snapshot.Secondary) AppendWindow(body, label + " secondary", bucket.Secondary, now);
        }
        body.Append("\nAvailable earned resets at observation: ").Append(snapshot.ResetCredits?.AvailableCount.ToString(CultureInfo.InvariantCulture) ?? "Not reported")
            .Append(".\n\n## Recent weekly observations\n\nUp to 20 measured points, without projected values.\n\n| Observed UTC | Remaining |\n| --- | ---: |\n");
        foreach (var point in view.WeeklyHistory.TakeLast(20))
            body.Append("| ").Append(Utc(point.RecordedAt)).Append(" | ").Append(point.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture)).Append("% |\n");
        body.Append("\n## Locally observed daily tokens\n\nThese are local activity totals, not account-wide billing or quota percentages.\n\n");
        body.Append("Availability: ").Append(view.TokenUsage.Status).Append(".\n\n| Local calendar date | Tokens |\n| --- | ---: |\n");
        foreach (var day in view.TokenUsage.Days.TakeLast(8))
            body.Append("| ").Append(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(day.TotalTokens.ToString("N0", CultureInfo.InvariantCulture)).Append(" |\n");
        return body.ToString();
    }

    private static void AppendWindow(StringBuilder body, string label, RateLimitWindow? window, DateTimeOffset now)
    {
        body.Append("| ").Append(UsageText.EscapeMarkdown(label));
        if (window is null) { body.Append(" | Not reported | Not reported | Unknown |\n"); return; }
        var valid = double.IsFinite(window.UsedPercent) && window.UsedPercent is >= 0 and <= 100 && window.WindowMinutes > 0;
        body.Append(" (").Append(window.WindowMinutes.ToString(CultureInfo.InvariantCulture)).Append(" minutes) | ")
            .Append(valid ? window.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "Invalid")
            .Append(" | ").Append(Utc(window.ResetsAt)).Append(" | ")
            .Append(!valid ? "Invalid" : window.ResetsAt <= now ? "Reset passed; refresh required" : "Active window").Append(" |\n");
    }

    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
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
