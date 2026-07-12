using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class CodexUsageDockPage : ContentPage, IDisposable
{
    private readonly CodexUsageService _usage;

    public CodexUsageDockPage(CodexUsageService usage)
    {
        _usage = usage;
        _usage.Updated += OnUpdated;
        Id = "nl.mathijs.codexusage.details";
        Icon = new IconInfo("\uE943");
        Title = "Codex Usage";
        Name = "Open";
        Commands =
        [
            new CommandContextItem(new RefreshUsageCommand(_usage))
            {
                Title = "Nu vernieuwen",
            },
        ];
    }

    public override IContent[] GetContent()
    {
        var snapshot = _usage.Current;
        return
        [
            new MarkdownContent($"""
                # Codex Usage

                {FormatWindow("5 uur", snapshot.Primary)}
                {FormatWindow("Week", snapshot.Secondary)}

                - **Abonnement:** {snapshot.PlanType ?? "onbekend"}
                - **Credits:** {snapshot.Credits ?? "niet beschikbaar"}
                - **Actieve taken:** {snapshot.ActiveThreads?.ToString(CultureInfo.CurrentCulture) ?? "onbekend"}
                - **Bijgewerkt:** {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss}

                _Bron: {snapshot.Source}{FormatError(snapshot.Error)}_
                """),
        ];
    }

    private static string FormatWindow(string name, RateLimitWindow? window)
    {
        if (window is null)
        {
            return $"- **{name}:** niet beschikbaar";
        }

        return $"- **{name}:** {window.RemainingPercent:0}% beschikbaar — {window.UsedPercent:0}% gebruikt · reset {window.ResetsAt.ToLocalTime():ddd d MMM HH:mm}";
    }

    private static string FormatError(string? error) => error is null ? string.Empty : $"  \n⚠ {error}";

    private void OnUpdated(object? sender, EventArgs e) => RaiseItemsChanged(0);

    public void Dispose()
    {
        _usage.Updated -= OnUpdated;
        GC.SuppressFinalize(this);
    }
}
