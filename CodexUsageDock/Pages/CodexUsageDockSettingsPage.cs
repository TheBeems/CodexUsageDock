using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.CmdPal.Common.Commands;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageDock;

internal sealed partial class CodexUsageDockSettingsPage : ContentPage
{
    private const string InvalidSourcePath = "Invalid source path: re-enter or clear this field";
    private const string ShowFiveHourLimitKey = "showFiveHourLimit";
    private const string ShowWeeklyLimitKey = "showWeeklyLimit";
    private const string ShowResetsAndCreditsKey = "showResetsAndCredits";
    private const string ShowResetTimeKey = "showResetTime";
    private const string RefreshIntervalKey = "refreshInterval";
    private const string UseAdaptiveWeeklyForecastKey = "useAdaptiveWeeklyForecast";
    private const string EnableUsageAlertsKey = "enableUsageAlerts";
    private const string CompactDockKey = "compactDock";
    private const string SeparateDockItemsKey = "separateDockItems";
    private const string ShowAccountActivityKey = "showAccountActivity";
    private const string CodexExecutablePathKey = "codexExecutablePath";
    private const string CodexHomePathKey = "codexHomePath";
    private const int MaximumPathLength = 1024;
    private const string HistoryRetentionKey = "historyRetentionDays";
    private const string WorkdayEndKey = "workdayEnd";
    private const string RemainingWorkdaysKey = "remainingWorkdays";
    private const string SourceLabelKey = "sourceLabel";
    private const string EnableClaudeKey = "enableClaude";
    private const string ClaudeBridgePathKey = "claudeBridgePath";
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
        _settings.Add(new ToggleSetting(EnableUsageAlertsKey, false)
        {
            Label = "Enable usage alerts",
            Description = "Show a quiet notification when the usage status changes.",
        });
        _settings.Add(new ToggleSetting(CompactDockKey, false)
        {
            Label = "Compact Dock",
            Description = "Use shorter usage labels and hide reset times in the Dock.",
        });
        _settings.Add(new ToggleSetting(SeparateDockItemsKey, false)
        {
            Label = "Separate Dock items",
            Description = "Show each usage item as its own Dock entry.",
        });
        _settings.Add(new ToggleSetting(ShowAccountActivityKey, true)
        {
            Label = "Show account activity",
            Description = "Read account-wide daily tokens from the Codex service after quotas load. Older CLI versions may not support this.",
        });
        _settings.Add(new TextSetting(CodexExecutablePathKey, string.Empty)
        {
            Label = "Codex executable path",
            Description = "Optional explicit path to codex.exe or codex.cmd.",
            Placeholder = @"C:\Path\to\codex.exe",
            Multiline = false,
        });
        _settings.Add(new TextSetting(CodexHomePathKey, string.Empty)
        {
            Label = "Codex home path",
            Description = "Optional Codex home directory. It may be a Windows-accessible WSL directory; this extension does not launch WSL or modify Codex configuration.",
            Placeholder = @"C:\Users\you\.codex",
            Multiline = false,
        });
        _settings.Add(new TextSetting(SourceLabelKey, "Default")
        {
            Label = "Source label",
            Description = "An optional name for these source paths. A label does not verify the signed-in account.",
            Multiline = false,
        });
        _settings.Add(new ToggleSetting(EnableClaudeKey, false)
        {
            Label = "Enable Claude usage pilot",
            Description = "Read only the local quota snapshot produced by your optional Claude statusline bridge.",
        });
        _settings.Add(new TextSetting(ClaudeBridgePathKey, string.Empty)
        {
            Label = "Claude bridge file",
            Description = "The full path to your bridge JSON file. Configure the optional statusline bridge before enabling this pilot.",
            Multiline = false,
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
        _settings.Add(new ChoiceSetSetting(HistoryRetentionKey,
        [new("Collection paused", "0"), new("7 days", "7"), new("30 days", "30"), new("90 days", "90")])
        {
            Label = "Retain usage observations",
            Description = "Optional local quota history for export. Pausing keeps saved data; use History to delete it.",
        });
        _settings.Add(new TextSetting(WorkdayEndKey, "17:00")
        {
            Label = "Workday end (HH:mm)",
            Description = "Local time used by the planner for today. After this time, planning pauses until the next day.",
            Multiline = false,
        });
        _settings.Add(new ChoiceSetSetting(RemainingWorkdaysKey,
        [new("1 workday", "1"), new("2 workdays", "2"), new("3 workdays", "3"), new("4 workdays", "4"),
         new("5 workdays", "5"), new("6 workdays", "6"), new("7 workdays", "7")])
        {
            Label = "Workdays remaining before weekly reset",
            Description = "Your planning assumption, including today. The extension does not infer your calendar.",
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

    public bool EnableUsageAlerts => _settings.GetSetting<bool>(EnableUsageAlertsKey);

    public bool CompactDock => _settings.GetSetting<bool>(CompactDockKey);

    public bool SeparateDockItems => _settings.GetSetting<bool>(SeparateDockItemsKey);

    public bool ShowAccountActivity => _settings.GetSetting<bool>(ShowAccountActivityKey);

    public string CodexExecutablePath => GetPathSetting(CodexExecutablePathKey);

    public string CodexHomePath => GetPathSetting(CodexHomePathKey);

    internal string SourceLabel => UsageText.SanitizeExternal(_settings.GetSetting<string>(SourceLabelKey), 40) ?? "Default";
    internal bool EnableClaude => _settings.GetSetting<bool>(EnableClaudeKey);
    internal string ClaudeBridgePath => GetPathSetting(ClaudeBridgePathKey);
    internal string ProfileStoragePath => Path.Combine(Path.GetDirectoryName(_path)!, "profiles.json");

    internal void ApplySourceProfile(string label, CodexSourceOptions options)
    {
        _settings.Update(new JsonObject
        {
            [SourceLabelKey] = UsageText.SanitizeExternal(label, 40) ?? "Custom",
            [CodexExecutablePathKey] = options.ExecutablePath ?? string.Empty,
            [CodexHomePathKey] = options.HomePath ?? string.Empty,
        }.ToJsonString());
        OnSettingsChanged(_settings, _settings);
    }

    public TimeSpan RefreshInterval => ParseRefreshInterval(_settings.GetSetting<string>(RefreshIntervalKey));

    internal int HistoryRetentionDays => _settings.GetSetting<string>(HistoryRetentionKey) switch
    { "7" => 7, "30" => 30, "90" => 90, _ => 0 };
    internal TimeOnly WorkdayEnd => TimeOnly.TryParseExact(_settings.GetSetting<string>(WorkdayEndKey), "HH:mm",
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time) ? time : new(17, 0);
    internal int RemainingWorkdays => int.TryParse(_settings.GetSetting<string>(RemainingWorkdaysKey), out var days)
        && days is >= 1 and <= 7 ? days : 1;

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
                if (property.Name is CodexExecutablePathKey or CodexHomePathKey or ClaudeBridgePathKey)
                {
                    valid[property.Name] = property.Value.ValueKind == JsonValueKind.String && IsValidPathSetting(property.Value.GetString())
                        ? property.Value.GetString() : InvalidSourcePath;
                    continue;
                }
                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && IsBooleanSetting(property.Name))
                {
                    valid[property.Name] = property.Value.GetBoolean() ? "true" : "false";
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = property.Value.GetString();
                if (property.Name == SourceLabelKey)
                {
                    valid[property.Name] = UsageText.SanitizeExternal(value, 40) ?? "Default";
                }
                else if (property.Name == RefreshIntervalKey && value is "1" or "5" or "15")
                {
                    valid[property.Name] = value;
                }
                else if (property.Name == HistoryRetentionKey && value is "0" or "7" or "30" or "90"
                    || property.Name == RemainingWorkdaysKey && value is "1" or "2" or "3" or "4" or "5" or "6" or "7"
                    || property.Name == WorkdayEndKey && TimeOnly.TryParseExact(value, "HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                {
                    valid[property.Name] = value;
                }
                else if (IsBooleanSetting(property.Name) && bool.TryParse(value, out var enabled))
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

    private static bool IsBooleanSetting(string name) => name is
        ShowFiveHourLimitKey or ShowWeeklyLimitKey or ShowResetsAndCreditsKey or ShowResetTimeKey or
        UseAdaptiveWeeklyForecastKey or EnableUsageAlertsKey or CompactDockKey or SeparateDockItemsKey or
        ShowAccountActivityKey or EnableClaudeKey;

    private static bool IsValidPathSetting(string? value)
    {
        if (value is null || value.Length > MaximumPathLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private string GetPathSetting(string key)
    {
        var value = _settings.GetSetting<string>(key);
        return IsValidPathSetting(value) ? value! : InvalidSourcePath;
    }

    private void OnSettingsChanged(object sender, Settings args)
    {
        var saved = LocalStorage.TryWrite(_path, _settings.ToJson());
        ShowOperationStatus(saved ? null : "Settings apply now but could not be saved. Try saving again before restarting.");
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
