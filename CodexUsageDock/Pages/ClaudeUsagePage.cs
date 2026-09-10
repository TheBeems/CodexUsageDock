using System.Globalization;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class ClaudeUsagePage : ContentPage, IDisposable
{
    private readonly CodexUsageService _service;
    private readonly object _gate = new();
    private MarkdownContent _content = new(string.Empty);
    private bool _disposed;
    internal ClaudeUsagePage(CodexUsageService service)
    {
        _service = service;
        Id = "nl.mathijs.codexusage.claude";
        Name = "Open";
        Title = "Claude usage pilot";
        Icon = new IconInfo("\uE943");
        service.ClaudeUpdated += OnUpdated;
        Refresh();
    }

    public override IContent[] GetContent() { lock (_gate) { return [_content]; } }

    internal static string Format(ClaudeUsageSnapshot snapshot)
    {
        var body = new StringBuilder("# Claude usage pilot\n\n").Append(snapshot.Message).Append("\n\n")
            .Append("These independent Claude quotas come from your explicitly selected local statusline capture. ")
            .Append("The bridge does not verify the Claude account and its percentages are never added to Codex usage.\n\n");
        if (snapshot.ObservedAt != DateTimeOffset.MinValue)
            body.Append("Observed UTC: ").Append(snapshot.ObservedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .Append(". Status: ").Append(snapshot.Status).Append(".\n\n");
        body.Append("| Claude window | Remaining at observation | Reset UTC |\n| --- | ---: | --- |\n");
        AppendWindow(body, "Five-hour", snapshot.Primary);
        AppendWindow(body, "Seven-day", snapshot.Weekly);
        return body.Append("\nSetup: use the optional capture script described in the repository README, select its output file in settings, ")
            .Append("then enable the pilot. The extension does not change Claude configuration or an existing statusline. ")
            .Append("Missing or expired windows remain unavailable until Claude emits a new capture.").ToString();
    }

    private static void AppendWindow(StringBuilder body, string name, ClaudeUsageWindow? window)
    {
        body.Append("| ").Append(name).Append(" | ")
            .Append(window is null ? "Not reported" : window.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%")
            .Append(" | ").Append(window?.ResetsAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "Not reported").Append(" |\n");
    }

    private void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var body = Format(_service.GetClaudeUsage());
            _content = new MarkdownContent(body);
            Commands = [new CommandContextItem(new RefreshUsageCommand(_service)) { Title = "Refresh captures" },
                new CommandContextItem(new CopyTextCommand(body)) { Title = "Copy Claude usage" }];
        }
        RaiseItemsChanged(0);
    }
    private void OnUpdated(object? sender, EventArgs args) => Refresh();
    public void Dispose() { lock (_gate) { _disposed = true; _service.ClaudeUpdated -= OnUpdated; } }
}
