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
    private readonly CodexUsageDiagnosticsPage _diagnostics;
    private readonly CodexAccountActivityPage _accountActivity;
    private readonly CodexPlanningPage _planner;
    private readonly CodexHistoryPage _history;
    private readonly CodexActionsPage _actions;
    private readonly CodexUsageTablePage _textUsage;
    private readonly CodexProfilesPage _profiles;
    private readonly ClaudeUsagePage _claude;
    private readonly ListItem _claudeFiveHour;
    private readonly ListItem _claudeWeekly;
    private readonly object _claudePresentationLock = new();
    private readonly UsageAlertEvaluator _alerts = new();
    private readonly Action<string> _notify;
    private readonly Func<DateTimeOffset> _clock;
    private bool _lastAlertsEnabled;
    private const string FiveHourDockId = "nl.mathijs.codexusage.dock.five-hour";
    private const string WeeklyDockId = "nl.mathijs.codexusage.dock.weekly";
    private const string CreditsDockId = "nl.mathijs.codexusage.dock.credits";
    private const string ClaudeDockId = "nl.mathijs.codexusage.dock.claude";
    private ICommandItem[] _dockBands = [];

    public CodexUsageDockCommandsProvider()
        : this(new CodexUsageService(), new CodexUsageDockSettingsPage())
    {
    }

    internal CodexUsageDockCommandsProvider(CodexUsageService usage, CodexUsageDockSettingsPage settings,
        Action<string>? notify = null, Func<DateTimeOffset>? clock = null)
    {
        _usage = usage;
        _settings = settings;
        _settings.Id = "nl.mathijs.codexusage.settings";
        _notify = notify ?? (message => new ToastStatusMessage(message).Show());
        _clock = clock ?? (() => DateTimeOffset.Now);
        _lastAlertsEnabled = _settings.EnableUsageAlerts;
        DisplayName = "Codex Usage";
        Id = "nl.mathijs.codexusage";
        Icon = new IconInfo("\uE943");

        _usage.SetRefreshInterval(_settings.RefreshInterval);
        _usage.SetAdaptiveWeeklyForecastEnabled(_settings.UseAdaptiveWeeklyForecast);
        ApplySourceSettings();
        _usage.SetAccountActivityEnabled(_settings.ShowAccountActivity);
        _usage.SetAggregateRetentionDays(_settings.HistoryRetentionDays);
        _usage.ConfigureClaude(_settings.EnableClaude, _settings.ClaudeBridgePath);
        var details = _details = new CodexUsageDockPage(_usage, _settings);
        _diagnostics = new CodexUsageDiagnosticsPage(_usage);
        _diagnostics.Id = "nl.mathijs.codexusage.diagnostics";
        _accountActivity = new CodexAccountActivityPage(_usage);
        _planner = new CodexPlanningPage(_usage, _settings, _clock);
        _history = new CodexHistoryPage(_usage);
        _actions = new CodexActionsPage(_usage);
        _textUsage = new CodexUsageTablePage(_usage, _clock);
        _profiles = new CodexProfilesPage(new CodexProfileStore(_settings.ProfileStoragePath));
        _profiles.ProfileSelected += OnProfileSelected;
        _claude = new ClaudeUsagePage(_usage);
        _claudeFiveHour = new ListItem(_claude);
        _claudeWeekly = new ListItem(_claude);
        _details.Commands = [.. _details.Commands, new CommandContextItem(_textUsage) { Title = "Read usage in text" }];
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
            new CommandItem(_diagnostics)
            {
                Title = "Codex Usage diagnostics",
                Subtitle = "Source, freshness, supported fields, and safe troubleshooting details",
            },
            new CommandItem(_accountActivity)
            {
                Title = "Codex account activity",
                Subtitle = "Account-wide daily tokens reported by Codex",
            },
            new CommandItem(_planner) { Title = "Codex workday planner", Subtitle = "Daily quota budget, recent pace, and forecast evidence" },
            new CommandItem(_history) { Title = "Codex usage history", Subtitle = "Retained quota observations, CSV/JSON export, and deletion" },
            new CommandItem(_actions) { Title = "Codex task usage and earned resets", Subtitle = "Request a task estimate or explicitly use an earned reset" },
            new CommandItem(_textUsage) { Title = "Codex usage in text", Subtitle = "Quota tables and measured values without charts or color cues" },
            new CommandItem(_profiles) { Title = "Codex source profiles", Subtitle = "Save and select named local or Windows-accessible WSL sources" },
            new CommandItem(_claude) { Title = "Claude usage pilot", Subtitle = "Optional local statusline capture; independent Claude quotas" },
        ];

        _settings.Changed += OnSettingsChanged;
        _settings.ClearAdaptiveHistoryRequested += OnClearAdaptiveHistoryRequested;
        _usage.Updated += OnUsageUpdated;
        _usage.ClaudeUpdated += OnClaudeUpdated;
        RefreshClaudeItems();
        RebuildDockBands();

        _usage.Start();
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => _dockBands;

    public override ICommandItem? GetCommandItem(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var known = _commands.Concat(_dockBands).FirstOrDefault(item => item.Command.Id == id);
        if (known is not null) return known;
        return id switch
        {
            "nl.mathijs.codexusage.dock" => new WrappedDockItem(GetVisibleDockItems(), "nl.mathijs.codexusage.dock", DisplayName),
            FiveHourDockId => new WrappedDockItem([_fiveHour], FiveHourDockId, "Codex five-hour usage"),
            WeeklyDockId => new WrappedDockItem([_weekly], WeeklyDockId, "Codex weekly usage"),
            CreditsDockId => new WrappedDockItem([_resetsAndCredits], CreditsDockId, "Codex resets and credits"),
            ClaudeDockId => new WrappedDockItem(_settings.EnableClaude ? [_claudeFiveHour, _claudeWeekly] : [], ClaudeDockId, "Claude usage"),
            _ => null,
        };
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_lastAlertsEnabled != _settings.EnableUsageAlerts)
        {
            _alerts.Reset();
            _lastAlertsEnabled = _settings.EnableUsageAlerts;
        }
        _usage.SetRefreshInterval(_settings.RefreshInterval);
        _usage.SetAdaptiveWeeklyForecastEnabled(_settings.UseAdaptiveWeeklyForecast);
        var sourceChanged = ApplySourceSettings();
        _usage.SetAccountActivityEnabled(_settings.ShowAccountActivity);
        _usage.SetAggregateRetentionDays(_settings.HistoryRetentionDays);
        _usage.ConfigureClaude(_settings.EnableClaude, _settings.ClaudeBridgePath);
        _fiveHour.Refresh();
        _weekly.Refresh();
        _details.Refresh();
        _planner.Refresh();
        _history.Refresh();
        RebuildDockBands();
        RaiseItemsChanged();
        if (sourceChanged) _ = _usage.RefreshAsync();
    }

    private bool ApplySourceSettings()
    {
        _ = CodexSourceOptions.TryCreate(_settings.CodexExecutablePath, _settings.CodexHomePath, out var options, out var error);
        if (error is not null) _settings.ShowOperationStatus(error);
        return _usage.ConfigureSource(options, error);
    }

    private void OnProfileSelected(object? sender, CodexProfileSelectedEventArgs args) => _settings.ApplySourceProfile(args.Name, args.Options);

    private void OnClaudeUpdated(object? sender, EventArgs args)
    {
        RefreshClaudeItems();
        RebuildDockBands();
        RaiseItemsChanged();
    }

    private void RefreshClaudeItems()
    {
        lock (_claudePresentationLock)
        {
            var snapshot = _usage.GetClaudeUsage();
            var now = _clock();
            var fresh = snapshot.IsAvailable && UsageFreshness.IsFresh(snapshot.ObservedAt, now, _usage.RefreshInterval);
            _claudeFiveHour.Title = "Claude 5h " + FormatClaudeRemaining(snapshot.Primary, fresh, now);
            _claudeWeekly.Title = "Claude week " + FormatClaudeRemaining(snapshot.Weekly, fresh, now);
            _claudeFiveHour.Subtitle = snapshot.Message;
            _claudeWeekly.Subtitle = snapshot.Message;
        }
    }

    private static string FormatClaudeRemaining(ClaudeUsageWindow? window, bool fresh, DateTimeOffset now) => fresh && window is not null && window.ResetsAt > now
        ? window.RemainingPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%" : "--";

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
        var alerts = _alerts.Evaluate(_usage.GetPresentation(), _clock(), _usage.RefreshInterval,
            new UsageAlertOptions(Enabled: _settings.EnableUsageAlerts));
        if (alerts.Count > 0)
        {
            var message = string.Join(" · ", alerts.Take(3).Select(alert => alert.Message));
            if (alerts.Count > 3) message += $" · {alerts.Count - 3} more usage alerts";
            _notify(message);
        }
    }

    private void RebuildDockBands()
    {
        var items = GetVisibleDockItems();
        ICommandItem[] bands;
        if (_settings.SeparateDockItems)
        {
            bands = items.Select(item => new WrappedDockItem([item],
                ReferenceEquals(item, _fiveHour) ? FiveHourDockId : ReferenceEquals(item, _weekly) ? WeeklyDockId : CreditsDockId,
                ReferenceEquals(item, _fiveHour) ? "Codex five-hour usage" : ReferenceEquals(item, _weekly) ? "Codex weekly usage" : "Codex resets and credits"))
                .Cast<ICommandItem>().ToArray();
        }
        else
        {
            var dockBand = items.Length == 0 ? null : new WrappedDockItem(items, "nl.mathijs.codexusage.dock", DisplayName);
            bands = dockBand is null ? [] : [dockBand];
        }
        if (_settings.EnableClaude)
            bands = [.. bands, new WrappedDockItem([_claudeFiveHour, _claudeWeekly], ClaudeDockId, "Claude usage")];
        _dockBands = bands;
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
        _usage.ClaudeUpdated -= OnClaudeUpdated;
        _profiles.ProfileSelected -= OnProfileSelected;
        _fiveHour.Dispose();
        _weekly.Dispose();
        _resetsAndCredits.Dispose();
        _details.Dispose();
        _diagnostics.Dispose();
        _accountActivity.Dispose();
        _planner.Dispose();
        _history.Dispose();
        _actions.Dispose();
        _textUsage.Dispose();
        _profiles.Dispose();
        _claude.Dispose();
        _usage.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
