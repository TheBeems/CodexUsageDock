using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal partial class UsageDockListItem(ICommand command) : ListItem(command)
{
    internal void NotifyDisplayPropertiesChanged()
    {
        // A host can subscribe after reading the initial values and miss an
        // intervening update. Refresh even unchanged values on the next read.
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Icon));
    }
}
