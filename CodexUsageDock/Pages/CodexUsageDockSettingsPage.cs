using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.CmdPal.Common.Commands;

namespace CodexUsageDock;

internal sealed partial class CodexUsageDockSettingsPage : ContentPage
{
    private const string ShowFiveHourLimitKey = "showFiveHourLimit";
    private const string ShowWeeklyLimitKey = "showWeeklyLimit";
    private const string ShowResetsAndCreditsKey = "showResetsAndCredits";
    private const string ShowResetTimeKey = "showResetTime";
    private const string RefreshIntervalKey = "refreshInterval";
    private const string UseAdaptiveWeeklyForecastKey = "useAdaptiveWeeklyForecast";
    private readonly Settings _settings = new();

    public CodexUsageDockSettingsPage()
    {
        Name = "Settings";
        Title = "Codex Usage settings";
        Icon = new IconInfo("\uE713");

        _settings.Add(new ToggleSetting(ShowFiveHourLimitKey, true)
        {
            Label = "Show five-hour limit",
            Description = "Show the five-hour usage limit in the Dock.",
        });
        _settings.Add(new ToggleSetting(ShowWeeklyLimitKey, true)
        {
            Label = "Show weekly limit",
            Description = "Show the weekly usage limit in the Dock.",
        });
        _settings.Add(new ToggleSetting(ShowResetsAndCreditsKey, true)
        {
            Label = "Show resets and credits",
            Description = "Show available resets and credits in the Dock.",
        });
        _settings.Add(new ToggleSetting(ShowResetTimeKey, true)
        {
            Label = "Show reset time",
            Description = "Show the next reset time below each usage limit.",
        });
        _settings.Add(new ToggleSetting(UseAdaptiveWeeklyForecastKey, true)
        {
            Label = "Use adaptive weekly forecast",
            Description = "Blend the current pace with up to eight local weekly cycles. Turning this off pauses learning and keeps saved history.",
        });
        _settings.Add(new ChoiceSetSetting(
            RefreshIntervalKey,
            [
                new ChoiceSetSetting.Choice("Every minute", "1"),
                new ChoiceSetSetting.Choice("Every 5 minutes", "5"),
                new ChoiceSetSetting.Choice("Every 15 minutes", "15"),
            ])
        {
            Label = "Refresh interval",
            Description = "How often the extension refreshes local Codex usage data.",
        });
        var clearHistory = new ConfirmableCommand(
            new AnonymousCommand(() => ClearAdaptiveHistoryRequested?.Invoke(this, EventArgs.Empty))
            {
                Result = CommandResult.KeepOpen(),
            },
            "Delete learned forecast history?",
            "This permanently removes local adaptive weekly forecast history from this device.",
            () => true)
        {
            Name = "Delete learned forecast history",
        };
        Commands =
        [
            new CommandContextItem(clearHistory)
            {
                Title = "Delete learned forecast history",
            },
        ];
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public event EventHandler? Changed;

    public event EventHandler? ClearAdaptiveHistoryRequested;

    public bool ShowFiveHourLimit => _settings.GetSetting<bool>(ShowFiveHourLimitKey);

    public bool ShowWeeklyLimit => _settings.GetSetting<bool>(ShowWeeklyLimitKey);

    public bool ShowResetsAndCredits => _settings.GetSetting<bool>(ShowResetsAndCreditsKey);

    public bool ShowResetTime => _settings.GetSetting<bool>(ShowResetTimeKey);

    public bool UseAdaptiveWeeklyForecast => _settings.GetSetting<bool>(UseAdaptiveWeeklyForecastKey);

    public TimeSpan RefreshInterval => ParseRefreshInterval(_settings.GetSetting<string>(RefreshIntervalKey));

    public override IContent[] GetContent() => _settings.ToContent();

    internal static TimeSpan ParseRefreshInterval(string? value) => value switch
    {
        "5" => TimeSpan.FromMinutes(5),
        "15" => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(1),
    };

    private void OnSettingsChanged(object sender, Settings args) => Changed?.Invoke(this, EventArgs.Empty);
}
