using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace CodexUsageDock.Tests;

[SuppressMessage("Performance", "CA1859:Use concrete types when possible",
    Justification = "These tests exercise host subscriptions through the public WinRT interface.")]
public sealed class DockNotificationTests
{
    [Theory]
    [InlineData(unchecked((int)0x80010108))]
    [InlineData(unchecked((int)0x800706BA))]
    public void DisconnectedSubscriberDoesNotBlockCurrentHost(int hresult)
    {
        var item = new UsageDockListItem(new NoOpCommand());
        IListItem hostItem = item;
        var disconnectedCalls = 0;
        var displayedTitle = string.Empty;
        hostItem.PropChanged += (_, _) =>
        {
            disconnectedCalls++;
            throw Marshal.GetExceptionForHR(hresult)!;
        };
        hostItem.PropChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ICommandItem.Title)) displayedTitle = hostItem.Title;
        };

        item.Title = "Week 93%";
        Assert.Equal("Week 93%", displayedTitle);
        item.Title = "Week 86%";
        item.NotifyDisplayPropertiesChanged();

        Assert.Equal("Week 86%", displayedTitle);
        Assert.Equal(1, disconnectedCalls);
    }

    [Fact]
    public void TransientSubscriberFailureAllowsOtherNotificationsAndRecovery()
    {
        var item = new UsageDockListItem(new NoOpCommand());
        IListItem hostItem = item;
        var fail = true;
        var recoveredTitle = string.Empty;
        var properties = new List<string>();
        hostItem.PropChanged += (_, args) =>
        {
            if (fail) throw Marshal.GetExceptionForHR(unchecked((int)0x8001010D))!;
            if (args.PropertyName == nameof(ICommandItem.Title)) recoveredTitle = hostItem.Title;
        };
        hostItem.PropChanged += (_, args) => properties.Add(args.PropertyName);

        item.Title = "Week 86%";
        item.NotifyDisplayPropertiesChanged();
        Assert.Contains(nameof(ICommandItem.Title), properties);
        Assert.Contains(nameof(ICommandItem.Subtitle), properties);
        Assert.Contains(nameof(ICommandItem.Icon), properties);

        fail = false;
        item.NotifyDisplayPropertiesChanged();
        Assert.Equal("Week 86%", recoveredTitle);
    }

    [Fact]
    public void UsageItemExposesIsolatedNotificationsThroughWinRtInterface()
    {
        var map = typeof(UsageDockItem).GetInterfaceMap(typeof(INotifyPropChanged));
        Assert.All(map.TargetMethods, method => Assert.Equal(typeof(UsageDockListItem), method.DeclaringType));
    }
}
