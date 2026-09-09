using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

public partial class CodexUsageDockCommandsProvider : CommandProvider
{
    private readonly CodexUsageDockSettingsPage _settings;
    private readonly CodexUsageService _usage;
    private readonly UsageDockItem _fiveHour;
    private readonly UsageDockItem _weekly;
    private readonly UsageDockItem _resetsAndCredits;
    private readonly ICommandItem[] _commands;
    private readonly CodexUsageDockPage _details;
    private ICommandItem[] _dockBands = [];

    public CodexUsageDockCommandsProvider()
        : this(new CodexUsageService(), new CodexUsageDockSettingsPage())
    {
    }

    internal CodexUsageDockCommandsProvider(CodexUsageService usage, CodexUsageDockSettingsPage settings)
    {
        _usage = usage;
        _settings = settings;
        DisplayName = "Codex Usage";
        Id = "nl.mathijs.codexusage";
        Icon = new IconInfo("\uE943");

        _usage.SetRefreshInterval(_settings.RefreshInterval);
        _usage.SetAdaptiveWeeklyForecastEnabled(_settings.UseAdaptiveWeeklyForecast);
        var details = _details = new CodexUsageDockPage(_usage, _settings);
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
        _settings.ClearAdaptiveHistoryRequested += OnClearAdaptiveHistoryRequested;
        _usage.Updated += OnUsageUpdated;
        RebuildDockBands();

        _usage.Start();
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => _dockBands;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _usage.SetRefreshInterval(_settings.RefreshInterval);
        _usage.SetAdaptiveWeeklyForecastEnabled(_settings.UseAdaptiveWeeklyForecast);
        _fiveHour.Refresh();
        _weekly.Refresh();
        _details.Refresh();
        RebuildDockBands();
        RaiseItemsChanged();
    }

    private void OnClearAdaptiveHistoryRequested(object? sender, EventArgs e)
    {
        var cleared = _usage.ClearAdaptiveWeeklyHistory();
        _settings.ShowOperationStatus(cleared
            ? "Learned forecast history deleted."
            : "Learned forecast history could not be deleted. Please try again.", cleared);
        _details.Refresh();
        if (cleared)
        {
            _ = _usage.RefreshAsync();
        }
    }

    private void OnUsageUpdated(object? sender, EventArgs e)
    {
        if (_usage.IsLoading)
        {
            return;
        }

        RebuildDockBands();
        RaiseItemsChanged();
    }

    private void RebuildDockBands()
    {
        var items = GetVisibleDockItems();
        var dockBand = items.Length == 0
            ? null
            : new WrappedDockItem(items, "nl.mathijs.codexusage.dock", DisplayName);
        _dockBands = dockBand is null ? [] : [dockBand];
    }

    private IListItem[] GetVisibleDockItems()
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

        return [.. items];
    }

    public override void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _settings.ClearAdaptiveHistoryRequested -= OnClearAdaptiveHistoryRequested;
        _usage.Updated -= OnUsageUpdated;
        _fiveHour.Dispose();
        _weekly.Dispose();
        _resetsAndCredits.Dispose();
        _details.Dispose();
        _usage.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
