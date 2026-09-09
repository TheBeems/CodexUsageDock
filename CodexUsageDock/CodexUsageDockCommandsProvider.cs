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
    private readonly UsageDockListItem _claudeFiveHour;
    private readonly UsageDockListItem _claudeWeekly;
    private readonly object _claudePresentationLock = new();
    private readonly UsageAlertEvaluator _alerts = new();
    private readonly Action<string> _notify;
    private readonly Func<DateTimeOffset> _clock;
    private bool _lastAlertsEnabled;
    private const string FiveHourDockId = "nl.mathijs.codexusage.dock.five-hour";
    private const string WeeklyDockId = "nl.mathijs.codexusage.dock.weekly";
    private const string CreditsDockId = "nl.mathijs.codexusage.dock.credits";
    private const string ClaudeDockId = "nl.mathijs.codexusage.dock.claude";
    private readonly object _dockLayoutLock = new();
    private readonly UsageDockBand _combinedBand;
    private readonly UsageDockBand _fiveHourBand;
    private readonly UsageDockBand _weeklyBand;
    private readonly UsageDockBand _creditsBand;
    private readonly UsageDockBand _claudeBand;
    private readonly UsageDockBand[] _allDockBands;
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
        _claudeFiveHour = new UsageDockListItem(_claude);
        _claudeWeekly = new UsageDockListItem(_claude);
        _details.Commands = [.. _details.Commands, new CommandContextItem(_textUsage) { Title = "Read usage in text" }];
        _fiveHour = new UsageDockItem(_usage, UsageDockItemKind.FiveHour, details, _settings);
        _weekly = new UsageDockItem(_usage, UsageDockItemKind.Weekly, details, _settings);
        _resetsAndCredits = new UsageDockItem(_usage, UsageDockItemKind.ResetsAndCredits, details);
        _combinedBand = new("nl.mathijs.codexusage.dock", DisplayName);
        _fiveHourBand = new(FiveHourDockId, "Codex five-hour usage");
        _weeklyBand = new(WeeklyDockId, "Codex weekly usage");
        _creditsBand = new(CreditsDockId, "Codex resets and credits");
        _claudeBand = new(ClaudeDockId, "Claude usage");
        _allDockBands = [_combinedBand, _fiveHourBand, _weeklyBand, _creditsBand, _claudeBand];

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
        UpdateDockLayout();

        _usage.Start();
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => [.. Volatile.Read(ref _dockBands)];

    public override ICommandItem? GetCommandItem(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _commands.Concat(Volatile.Read(ref _dockBands)).FirstOrDefault(item => item.Command.Id == id);
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
        UpdateDockLayout();
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
        if (_claudeBand.HasItems) _claudeBand.NotifyItemsChanged();
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

        foreach (var band in Volatile.Read(ref _dockBands).OfType<UsageDockBand>())
        {
            if (!ReferenceEquals(band, _claudeBand)) band.NotifyItemsChanged();
        }
        var alerts = _alerts.Evaluate(_usage.GetPresentation(), _clock(), _usage.RefreshInterval,
            new UsageAlertOptions(Enabled: _settings.EnableUsageAlerts));
        if (alerts.Count > 0)
        {
            var message = string.Join(" · ", alerts.Take(3).Select(alert => alert.Message));
            if (alerts.Count > 3) message += $" · {alerts.Count - 3} more usage alerts";
            _notify(message);
        }
    }

    private void UpdateDockLayout()
    {
        var changedBands = new List<UsageDockBand>();
        bool catalogChanged;
        lock (_dockLayoutLock)
        {
            var separate = _settings.SeparateDockItems;
            Publish(_combinedBand, separate ? [] : GetVisibleDockItems());
            Publish(_fiveHourBand, separate && _settings.ShowFiveHourLimit ? [_fiveHour] : []);
            Publish(_weeklyBand, separate && _settings.ShowWeeklyLimit ? [_weekly] : []);
            Publish(_creditsBand, separate && _settings.ShowResetsAndCredits ? [_resetsAndCredits] : []);
            Publish(_claudeBand, _settings.EnableClaude ? [_claudeFiveHour, _claudeWeekly] : []);
            ICommandItem[] bands = _allDockBands.Where(band => band.HasItems).ToArray();
            catalogChanged = !Volatile.Read(ref _dockBands).SequenceEqual(bands);
            Volatile.Write(ref _dockBands, bands);
        }

        // No host callback may run while the layout lock is held.
        foreach (var band in changedBands) band.NotifyItemsChanged();
        if (catalogChanged) RaiseItemsChanged();

        void Publish(UsageDockBand band, IListItem[] items)
        {
            if (band.PublishItems(items)) changedBands.Add(band);
        }
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
