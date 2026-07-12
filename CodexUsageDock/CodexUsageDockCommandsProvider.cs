using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

public partial class CodexUsageDockCommandsProvider : CommandProvider
{
    private readonly CodexUsageService _usage = new();
    private readonly UsageDockItem _fiveHour;
    private readonly UsageDockItem _weekly;
    private readonly UsageDockItem _resetsAndCredits;
    private readonly ICommandItem[] _commands;
    private readonly ICommandItem[] _dockBands;

    public CodexUsageDockCommandsProvider()
    {
        DisplayName = "Codex Usage";
        Id = "nl.mathijs.codexusage";
        Icon = new IconInfo("\uE943");

        var details = new CodexUsageDockPage(_usage);
        _fiveHour = new UsageDockItem(_usage, UsageDockItemKind.FiveHour, details);
        _weekly = new UsageDockItem(_usage, UsageDockItemKind.Weekly, details);
        _resetsAndCredits = new UsageDockItem(_usage, UsageDockItemKind.ResetsAndCredits, details);

        _commands =
        [
            new CommandItem(details)
            {
                Title = DisplayName,
                Subtitle = "Limits, resets, and Codex status",
            },
        ];

        _dockBands =
        [
            new WrappedDockItem(
                [_fiveHour, _weekly, _resetsAndCredits],
                "nl.mathijs.codexusage.dock",
                DisplayName),
        ];

        _usage.Start();
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => _dockBands;

    public override void Dispose()
    {
        _fiveHour.Dispose();
        _weekly.Dispose();
        _resetsAndCredits.Dispose();
        _usage.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
