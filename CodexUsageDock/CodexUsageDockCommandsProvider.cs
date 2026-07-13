using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

public partial class CodexUsageDockCommandsProvider : CommandProvider
{
    private readonly CodexUsageDockSettingsPage _settings = new();
    private readonly CodexUsageService _usage = new();
    private readonly UsageDockItem _fiveHour;
    private readonly UsageDockItem _weekly;
    private readonly UsageDockItem _resetsAndCredits;
    private readonly ICommandItem[] _commands;
    private ICommandItem[] _dockBands = [];

    public CodexUsageDockCommandsProvider()
    {
        DisplayName = "Codex Usage";
        Id = "nl.mathijs.codexusage";
        Icon = new IconInfo("\uE943");

        var details = new CodexUsageDockPage(_usage);
        _fiveHour = new UsageDockItem(_usage, UsageDockItemKind.FiveHour, details, _settings);
        _weekly = new UsageDockItem(_usage, UsageDockItemKind.Weekly, details, _settings);
        _resetsAndCredits = new UsageDockItem(_usage, UsageDockItemKind.ResetsAndCredits, details);

        _commands =
        [
            new CommandItem(details)
            {
                Title = DisplayName,
                Subtitle = "Limits, resets, and Codex status",
            },
            new CommandItem(_settings)
            {
                Title = "Codex Usage settings",
                Subtitle = "Choose what appears in the Dock",
                Icon = new IconInfo("\uE713"),
            },
        ];

        _settings.Changed += OnSettingsChanged;
        RebuildDockBands();

        _usage.Start();
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => _dockBands;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _usage.SetRefreshInterval(_settings.RefreshInterval);
        _fiveHour.Refresh();
        _weekly.Refresh();
        RebuildDockBands();
        RaiseItemsChanged();
    }

    private void RebuildDockBands()
    {
        var items = new List<IListItem>(3);
        if (_settings.ShowFiveHourLimit)
        {
            items.Add(_fiveHour);
        }

        if (_settings.ShowWeeklyLimit)
        {
            items.Add(_weekly);
        }

        if (_settings.ShowResetsAndCredits)
        {
            items.Add(_resetsAndCredits);
        }

        _dockBands = items.Count == 0
            ? []
            : [new WrappedDockItem([.. items], "nl.mathijs.codexusage.dock", DisplayName)];
    }

    public override void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _fiveHour.Dispose();
        _weekly.Dispose();
        _resetsAndCredits.Dispose();
        _usage.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
