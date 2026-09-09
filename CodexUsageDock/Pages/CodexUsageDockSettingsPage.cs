using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.CmdPal.Common.Commands;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    private readonly string _path;
    private readonly FormContent _statusContent = new()
    {
        TemplateJson = """
        {"type":"AdaptiveCard","version":"1.5","body":[{"type":"TextBlock","text":"${message}","color":"${color}","wrap":true}]}
        """,
    };

    public CodexUsageDockSettingsPage()
        : this(LocalStorage.GetPath("settings.json"))
    {
    }

    internal CodexUsageDockSettingsPage(string path)
    {
        _path = Path.GetFullPath(path);
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
            "This permanently removes learned forecast history for the currently identified account and quota category. Other accounts are kept.",
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
        Load();
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

    internal string? StatusMessage { get; private set; }

    public override IContent[] GetContent() => StatusMessage is null
        ? _settings.ToContent()
        : [_statusContent, .. _settings.ToContent()];

    internal void ShowOperationStatus(string? message, bool succeeded = false)
    {
        StatusMessage = message;
        _statusContent.DataJson = new JsonObject { ["message"] = message, ["color"] = succeeded ? "Good" : "Attention" }.ToJsonString();
        RaiseItemsChanged(0);
    }

    internal static TimeSpan ParseRefreshInterval(string? value) => value switch
    {
        "5" => TimeSpan.FromMinutes(5),
        "15" => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(1),
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Settings must be an object.");
            }

            var valid = new JsonObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = property.Value.GetString();
                if (property.Name == RefreshIntervalKey && value is "1" or "5" or "15")
                {
                    valid[property.Name] = value;
                }
                else if (property.Name is ShowFiveHourLimitKey or ShowWeeklyLimitKey or ShowResetsAndCreditsKey or ShowResetTimeKey or UseAdaptiveWeeklyForecastKey
                    && bool.TryParse(value, out var enabled))
                {
                    valid[property.Name] = enabled ? "true" : "false";
                }
            }

            _settings.Update(valid.ToJsonString());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            LocalStorage.TraceFailure("load settings", error);
            ShowOperationStatus("Saved settings could not be read. Default settings are being used.");
        }
    }

    private void OnSettingsChanged(object sender, Settings args)
    {
        var saved = LocalStorage.TryWrite(_path, _settings.ToJson());
        ShowOperationStatus(saved ? null : "Settings apply now but could not be saved. Try saving again before restarting.");
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
