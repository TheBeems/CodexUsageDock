using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class UsageDockBand : CommandItem
{
    private readonly DockBandPage _page;

    internal UsageDockBand(string id, string title) : this(new DockBandPage(id, title)) { }

    private UsageDockBand(DockBandPage page) : base(page) => _page = page;

    internal bool HasItems => _page.HasItems;
    internal bool PublishItems(IListItem[] items) => _page.PublishItems(items);
    internal void NotifyItemsChanged() => _page.NotifyItemsChanged();

    private sealed partial class DockBandPage : ListPage
    {
        private IListItem[] _items = [];

        internal DockBandPage(string id, string title)
        {
            Id = id;
            Name = title;
            Title = title;
        }

        internal bool HasItems => Volatile.Read(ref _items).Length > 0;
        public override IListItem[] GetItems() => [.. Volatile.Read(ref _items)];

        // The host synchronously calls GetItems from ItemsChanged. Publish the
        // complete layout before notifying, including newly inactive bands.
        internal bool PublishItems(IListItem[] items)
        {
            if (Volatile.Read(ref _items).SequenceEqual(items)) return false;
            Volatile.Write(ref _items, items);
            return true;
        }

        internal void NotifyItemsChanged()
        {
            var items = Volatile.Read(ref _items);
            foreach (var item in items.OfType<UsageDockListItem>()) item.NotifyDisplayPropertiesChanged();
            RaiseItemsChanged(Volatile.Read(ref _items).Length);
        }
    }
}
