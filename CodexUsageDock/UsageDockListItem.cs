using System.Diagnostics;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace CodexUsageDock;

internal partial class UsageDockListItem : ListItem, INotifyPropChanged
{
    internal UsageDockListItem(ICommand command) : base(command)
    {
        // The pinned SDK stops multicast delivery after a failed COM subscriber.
        // Bridge its notifications so an old host cannot block the current one.
        base.PropChanged += ForwardPropertyChanged;
    }

    public new event TypedEventHandler<object, IPropChangedEventArgs>? PropChanged;

    private void ForwardPropertyChanged(object sender, IPropChangedEventArgs args)
    {
        var handlers = PropChanged;
        if (handlers is null) return;

        foreach (TypedEventHandler<object, IPropChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception error)
            {
                // Only permanent RPC failures remove a subscriber. A busy host
                // must still receive the next refresh after a transient error.
                if (error.HResult is unchecked((int)0x80010108) or unchecked((int)0x800706BA))
                {
                    PropChanged -= handler;
                }

                Trace.TraceWarning("Codex Usage Dock: display notification failed (HRESULT 0x{0:X8}).", error.HResult);
            }
        }
    }

    internal void NotifyDisplayPropertiesChanged()
    {
        // A host can subscribe after reading the initial values and miss an
        // intervening update. Refresh even unchanged values on the next read.
        ForwardPropertyChanged(this, new PropChangedEventArgs(nameof(Title)));
        ForwardPropertyChanged(this, new PropChangedEventArgs(nameof(Subtitle)));
        ForwardPropertyChanged(this, new PropChangedEventArgs(nameof(Icon)));
    }
}
