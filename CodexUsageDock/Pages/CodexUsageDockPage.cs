using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class CodexUsageDockPage : ListPage, IDisposable
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
        ShowDetails = true;
    }

    public override IListItem[] GetItems()
    {
        var snapshot = _usage.Current;
        return
        [
            CreateWindowItem("5 uur", snapshot.Primary),
            CreateWindowItem("Week", snapshot.Secondary),
            new ListItem(new NoOpCommand())
            {
                Title = $"Abonnement: {snapshot.PlanType ?? "onbekend"}",
                Subtitle = snapshot.Credits is null ? "Geen creditinformatie beschikbaar" : $"Credits: {snapshot.Credits}",
                Icon = new IconInfo("\uE77B"),
            },
            new ListItem(new NoOpCommand())
            {
                Title = snapshot.ActiveThreads is null ? "Actieve taken: onbekend" : $"Actieve taken: {snapshot.ActiveThreads}",
                Subtitle = $"Bron: {snapshot.Source} · bijgewerkt {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss}",
                Icon = new IconInfo("\uE9D9"),
            },
            new ListItem(new RefreshUsageCommand(_usage))
            {
                Title = "Nu vernieuwen",
                Subtitle = snapshot.Error,
                Icon = new IconInfo("\uE72C"),
            },
        ];
    }

    private static ListItem CreateWindowItem(string name, RateLimitWindow? window)
    {
        if (window is null)
        {
            return new ListItem(new NoOpCommand())
            {
                Title = $"{name}: niet beschikbaar",
                Icon = new IconInfo("\uE783"),
            };
        }

        return new ListItem(new NoOpCommand())
        {
            Title = $"{name}: {window.RemainingPercent:0}% beschikbaar",
            Subtitle = $"{window.UsedPercent:0}% gebruikt · reset {window.ResetsAt.ToLocalTime():ddd d MMM HH:mm}",
            Icon = new IconInfo("\uE916"),
        };
    }

    private void OnUpdated(object? sender, EventArgs e) => RaiseItemsChanged(0);

    public void Dispose()
    {
        _usage.Updated -= OnUpdated;
        GC.SuppressFinalize(this);
    }
}
