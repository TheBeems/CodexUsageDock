using System.Text.Json;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace CodexUsageDock.Tests;

// Contract checks only: the host Back stack and command bar require the manual UI test.
public sealed class NavigationContractTests
{
    private static readonly string[] DashboardActionTitles = ["Refresh now", "Codex Usage settings", "Usage information", "Read usage in text"];
#if DEBUG
    private static readonly string[] FixtureChildNames = ["Settings", "Usage information", "Read usage in text"];
#endif

    [Theory]
    [InlineData("nl.mathijs.codexusage.settings")]
    [InlineData("nl.mathijs.codexusage.information")]
    [InlineData("nl.mathijs.codexusage.table")]
    public void DashboardActionsSurviveChildContentReadsAndPresentationRefreshes(string childId)
    {
        using var environment = new TestEnvironment();
        using var service = environment.CreateService();
        using var provider = new CodexUsageDockCommandsProvider(service, environment.CreateSettings(), _ => { });
        var dashboard = Assert.IsType<CodexUsageDockPage>(provider.GetCommandItem("nl.mathijs.codexusage.details")!.Command);
        var originalActions = dashboard.Commands.ToArray();
        var originalTargets = originalActions.Cast<CommandContextItem>().Select(item => Assert.IsAssignableFrom<ICommand>(item.Command)).ToArray();
        Assert.Equal(DashboardActionTitles,
            originalActions.Cast<CommandContextItem>().Select(item => item.Title));
        var child = Assert.IsAssignableFrom<ContentPage>(originalTargets.Single(command => command.Id == childId));

        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.NotEmpty(child.GetContent());
            dashboard.Refresh();
            Assert.NotEmpty(dashboard.GetContent());
            Assert.Equal(originalActions.Length, dashboard.Commands.Length);
            for (var index = 0; index < originalActions.Length; index++)
            {
                Assert.Same(originalActions[index], dashboard.Commands[index]);
                Assert.Same(originalTargets[index], Assert.IsType<CommandContextItem>(dashboard.Commands[index]).Command);
            }
        }
    }

    [Fact]
    public void NavigationFixtureIsAvailableOnlyInDebugBuilds()
    {
        using var environment = new TestEnvironment();
        using var service = environment.CreateService();
        using var provider = new CodexUsageDockCommandsProvider(service, environment.CreateSettings(), _ => { });
        const string id = "nl.mathijs.codexusage.navigation-test";
#if DEBUG
        var item = Assert.Single(provider.TopLevelCommands(), item => item.Command.Id == id);
        Assert.Same(item, provider.GetCommandItem(id));
        Assert.IsType<NavigationTestPage>(item.Command);
#else
        Assert.Null(provider.GetCommandItem(id));
        Assert.DoesNotContain(provider.TopLevelCommands(), item => item.Command.Id == id);
        Assert.Null(typeof(CodexUsageDockCommandsProvider).Assembly.GetType("CodexUsageDock.NavigationTestPage"));
#endif
    }

#if DEBUG
    [Fact]
    public void FixtureUsesProductionPageShapesAndRetainsActionsDuringRefresh()
    {
        var dashboard = new NavigationTestPage();
        var form = Assert.IsType<FormContent>(Assert.Single(dashboard.GetContent()));
        var original = dashboard.Commands.ToArray();
        var children = original.Skip(1).Cast<CommandContextItem>()
            .Select(item => Assert.IsType<NavigationTestChildPage>(item.Command)).ToArray();
        Assert.Equal(FixtureChildNames, children.Select(child => child.Name));
        Assert.IsType<FormContent>(Assert.Single(children[0].GetContent()));
        Assert.Single(children[0].Commands);
        Assert.IsType<MarkdownContent>(Assert.Single(children[1].GetContent()));
        Assert.Empty(children[1].Commands);
        Assert.IsType<MarkdownContent>(Assert.Single(children[2].GetContent()));
        Assert.Equal(2, children[2].Commands.Length);

        var refresh = Assert.IsType<AnonymousCommand>(Assert.IsType<CommandContextItem>(original[0]).Command);
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            refresh.Invoke();
            using var data = JsonDocument.Parse(form.DataJson);
            Assert.Equal(cycle, data.RootElement.GetProperty("refreshCount").GetInt32());
            Assert.Same(form, Assert.Single(dashboard.GetContent()));
            for (var index = 0; index < original.Length; index++)
                Assert.Same(original[index], dashboard.Commands[index]);
        }
    }
#endif
}
