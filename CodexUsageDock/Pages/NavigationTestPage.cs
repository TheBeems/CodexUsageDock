#if DEBUG
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

// Exercises host navigation without usage updates, storage, or account dependencies.
internal sealed partial class NavigationTestPage : ContentPage
{
    internal const string CommandId = "nl.mathijs.codexusage.navigation-test";
    private readonly object _gate = new();
    private int _refreshCount;
    private readonly FormContent _content = new()
    {
        TemplateJson = """
        {"type":"AdaptiveCard","version":"1.5","body":[
          {"type":"TextBlock","text":"Navigation test dashboard","weight":"Bolder","size":"Large","wrap":true},
          {"type":"TextBlock","text":"Expected footer: Refresh test dashboard, Settings, More (or localized equivalents).","wrap":true},
          {"type":"TextBlock","text":"Open Settings, or More > Usage information / Read usage in text. Use the host Back arrow. All dashboard actions must return immediately.","wrap":true},
          {"type":"TextBlock","text":"Capture missing buttons before pressing Refresh. Child actions must not remain on this dashboard.","wrap":true},
          {"type":"TextBlock","text":"Refresh count: ${refreshCount}","wrap":true},
          {"type":"TextBlock","text":"Debug fixture. Fixed content; no automatic refresh. Test actions do not access account data, save settings, or copy to the clipboard.","isSubtle":true,"wrap":true}
        ]}
        """,
        DataJson = """{"refreshCount":0}""",
    };

    internal NavigationTestPage()
    {
        Id = CommandId;
        Name = "Open";
        Title = "Codex Usage navigation test";
        var settings = new NavigationTestChildPage("settings", "Settings", new FormContent
        {
            TemplateJson = """
            {"type":"AdaptiveCard","version":"1.5","body":[
              {"type":"TextBlock","text":"Test settings. Use Back to return; the dashboard Settings action must still be visible.","wrap":true},
              {"type":"Input.Toggle","id":"testOnly","title":"Test toggle (not saved)","value":"false"}
            ]}
            """,
        }, [HarmlessAction("Child settings action")]);
        var information = new NavigationTestChildPage("information", "Usage information",
            new MarkdownContent("# Test information\n\nThis page has no actions. Use Back: all dashboard actions must return."), []);
        var text = new NavigationTestChildPage("text", "Read usage in text",
            new MarkdownContent("# Test usage in text\n\nFixed example: 50% remaining. Use Back: the child Refresh and Copy actions must be replaced by the dashboard actions."),
            [HarmlessAction("Child refresh"), HarmlessAction("Child copy (no clipboard)")]);
        Commands =
        [
            new CommandContextItem(new AnonymousCommand(RefreshDashboard)
            {
                Name = "Refresh test dashboard", Result = CommandResult.KeepOpen(),
            }) { Title = "Refresh test dashboard" },
            new CommandContextItem(settings) { Title = settings.Title },
            new CommandContextItem(information) { Title = information.Title },
            new CommandContextItem(text) { Title = text.Title },
        ];
    }

    public override IContent[] GetContent() => [_content];

    private void RefreshDashboard()
    {
        lock (_gate)
        {
            _refreshCount++;
            _content.DataJson = new JsonObject { ["refreshCount"] = _refreshCount }.ToJsonString();
        }
        // Match the real dashboard: update content, without republishing Commands.
        RaiseItemsChanged(0);
    }

    private static CommandContextItem HarmlessAction(string name) => new(
        new AnonymousCommand(() => { }) { Name = name, Result = CommandResult.KeepOpen() }) { Title = name };
}

internal sealed partial class NavigationTestChildPage : ContentPage
{
    private readonly IContent _content;

    internal NavigationTestChildPage(string suffix, string title, IContent content, ICommandContextItem[] commands)
    {
        Id = $"{NavigationTestPage.CommandId}.{suffix}";
        Title = title;
        Name = title;
        _content = content;
        Commands = commands;
    }

    public override IContent[] GetContent() => [_content];
}
#endif
