using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class RefreshUsageCommand(CodexUsageService usage, bool refreshAccountActivity = false) : InvokableCommand
{
    public override string Name => "Refresh Codex usage";

    public override ICommandResult Invoke()
    {
        if (refreshAccountActivity) usage.RequestAccountUsageRefresh();
        _ = usage.RefreshAsync();
        return CommandResult.KeepOpen();
    }
}
