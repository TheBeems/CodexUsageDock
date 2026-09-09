using System.Globalization;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class CodexAccountActivityPage : ContentPage, IDisposable
{
    private readonly object _presentationLock = new();
    private readonly CodexUsageService _service;
    private MarkdownContent _content = new(string.Empty);
    private bool _disposed;

    internal CodexAccountActivityPage(CodexUsageService service)
    {
        _service = service;
        Id = "nl.mathijs.codexusage.account-activity";
        Name = "Open";
        Title = "Codex account activity";
        Icon = new IconInfo("\uE9D9");
        _service.Updated += OnUpdated;
        UpdatePresentation();
    }

    public override IContent[] GetContent()
    {
        lock (_presentationLock)
        {
            return [_content];
        }
    }

    internal static string FormatActivity(AccountUsageSnapshot snapshot)
    {
        var body = new StringBuilder("# Codex account activity\n\n");
        if (snapshot.Status == AccountUsageStatus.Unsupported)
        {
            body.Append("This Codex CLI does not provide account activity. An update may add support.\n");
            return body.ToString();
        }

        if (snapshot.Status == AccountUsageStatus.Unavailable)
        {
            body.Append("Account activity is unavailable. Enable account activity in settings and refresh to request it. The account must remain identified throughout the read.\n");
            return body.ToString();
        }

        body.Append("Token totals reported by the Codex account service. These figures do not measure your remaining quota.\n\n");
        if (snapshot.Status == AccountUsageStatus.Partial)
        {
            body.Append("**Partial data:** some server information is missing, invalid, duplicated, or outside the retained history.\n\n");
        }

        body.Append("Last read: ").Append(snapshot.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)).Append(".\n\n");
        AppendSummary(body, "Lifetime tokens", snapshot.LifetimeTokens);
        AppendSummary(body, "Peak daily tokens", snapshot.PeakDailyTokens);
        AppendSummary(body, "Longest running turn (seconds)", snapshot.LongestRunningTurnSeconds);
        AppendSummary(body, "Current streak (days)", snapshot.CurrentStreakDays);
        AppendSummary(body, "Longest streak (days)", snapshot.LongestStreakDays);

        body.Append("\n## Daily activity\n\nDates are calendar dates supplied by the server. Their time zone is not specified. Up to 30 recent reported days are shown.\n\n");
        if (snapshot.Days.Count == 0)
        {
            body.Append("No readable daily rows were returned. Missing days are not treated as zero usage.\n");
            return body.ToString();
        }

        body.Append("| Server date | Tokens |\n| --- | ---: |\n");
        foreach (var day in snapshot.Days.OrderByDescending(day => day.Date).Take(30))
        {
            body.Append("| ").Append(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(" | ").Append(day.TotalTokens.ToString("N0", CultureInfo.InvariantCulture)).Append(" |\n");
        }

        return body.ToString();
    }

    private static void AppendSummary(StringBuilder body, string label, long? value) =>
        body.Append("- **").Append(label).Append(":** ")
            .Append(value?.ToString("N0", CultureInfo.InvariantCulture) ?? "Not reported").Append('\n');

    private void UpdatePresentation()
    {
        var body = FormatActivity(_service.CurrentAccountUsage);
        lock (_presentationLock)
        {
            if (_disposed)
            {
                return;
            }

            _content = new MarkdownContent(body);
            Commands =
            [
                new CommandContextItem(new RefreshUsageCommand(_service, refreshAccountActivity: true)) { Title = "Refresh now" },
                new CommandContextItem(new CopyTextCommand(body)) { Title = "Copy activity summary" },
            ];
        }
    }

    private void OnUpdated(object? sender, EventArgs e)
    {
        UpdatePresentation();
        lock (_presentationLock)
        {
            if (_disposed)
            {
                return;
            }
        }

        RaiseItemsChanged(0);
    }

    public void Dispose()
    {
        lock (_presentationLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _service.Updated -= OnUpdated;
        }

        GC.SuppressFinalize(this);
    }
}
