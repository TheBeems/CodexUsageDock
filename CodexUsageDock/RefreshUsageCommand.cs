using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class RefreshUsageCommand(CodexUsageService usage) : InvokableCommand
{
    public override string Name => "Codex-gebruik vernieuwen";

    public override ICommandResult Invoke()
    {
        _ = usage.RefreshAsync();
        return CommandResult.KeepOpen();
    }
}
