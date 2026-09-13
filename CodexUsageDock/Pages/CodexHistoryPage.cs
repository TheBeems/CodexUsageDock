using System.Globalization;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.CmdPal.Common.Commands;

namespace CodexUsageDock;

internal sealed partial class CodexHistoryPage : ContentPage, IDisposable
{
    private readonly CodexUsageService _service;
    private readonly object _gate = new();
    private MarkdownContent _content = new(string.Empty);
    private string? _operation;
    private bool _disposed;

    internal CodexHistoryPage(CodexUsageService service)
    {
        _service = service;
        Id = "nl.mathijs.codexusage.history";
        Name = "Open";
        Title = "Codex usage history";
        Icon = new IconInfo("\uE81C");
        service.Updated += OnUpdated;
        Refresh();
    }

    public override IContent[] GetContent() { lock (_gate) { return [_content]; } }

    private void Export(bool csv, string context) { _operation = _service.ExportAggregateHistory(csv, context); Refresh(); }
    private void Clear(string context) { _operation = _service.ClearAggregateHistory(context) ? "Retained observations deleted." : "Retained observations could not be deleted, or the account/category changed."; Refresh(); }

    internal void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var history = _service.GetAggregateHistory();
            var body = new StringBuilder("# Usage history\n\n");
            if (_operation is not null) body.Append(UsageText.EscapeMarkdown(_operation)).Append("\n\n");
            if (!history.Identified) body.Append("Waiting for an identified account. Histories are separated by account and quota category.\n\n");
            body.Append(history.RetentionDays == 0 ? "Collection paused. Choose 7, 30, or 90 days in settings to retain observations."
                : $"Retention: {history.RetentionDays} days. Up to one observation per five minutes, with separate reset transitions.")
                .Append("\n\n").Append(history.Points.Count.ToString(CultureInfo.InvariantCulture)).Append(" retained observations. ")
                .Append("Exports contain UTC quota percentages and reset times, without account IDs, conversation content, tokens, or costs.\n\n");
            if (history.Error is not null) body.Append(history.Error).Append("\n\n");
            body.Append("The most recent 30 observations are shown. The exports include all retained rows for this context.\n\n")
                .Append("| Observed UTC | Five-hour remaining | Weekly remaining |\n| --- | ---: | ---: |\n");
            foreach (var point in history.Points.TakeLast(30).Reverse())
                body.Append("| ").Append(point.RecordedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(Percent(point.PrimaryRemainingPercent)).Append(" | ").Append(Percent(point.WeeklyRemainingPercent)).Append(" |\n");
            _content = new MarkdownContent(body.ToString());
            Commands = history.Context is not { } context ? [] :
            [
                new CommandContextItem(new AnonymousCommand(() => Export(true, context)) { Result = CommandResult.KeepOpen() }) { Title = "Export CSV file" },
                new CommandContextItem(new AnonymousCommand(() => Export(false, context)) { Result = CommandResult.KeepOpen() }) { Title = "Export JSON file" },
                new CommandContextItem(new ConfirmableCommand(new AnonymousCommand(() => Clear(context)) { Result = CommandResult.KeepOpen() },
                    "Delete retained usage observations?", "Delete this account and quota category's retained observations. Existing exports and learned forecasts are kept.", () => true)
                { Name = "Delete retained observations" }) { Title = "Delete retained observations" },
            ];
        }
        RaiseItemsChanged(0);
    }

    private static string Percent(double? value) => value?.ToString("0.#", CultureInfo.InvariantCulture) is { } text ? text + "%" : "Not reported";
    private void OnUpdated(object? sender, EventArgs args) => Refresh();
    public void Dispose() { lock (_gate) { _disposed = true; _service.Updated -= OnUpdated; } }
}
