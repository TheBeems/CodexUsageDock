using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class ProviderDockTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    public void Dispose() => _environment.Dispose();

    [Fact]
    public async Task TogglingAlertsOffAndOnWithoutARefreshEstablishesANewBaseline()
    {
        File.WriteAllText(_environment.PathFor("settings.json"), """{"enableUsageAlerts":"true"}""");
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(4);
        var remaining = 40.0;
        CodexUsageSnapshot Snapshot() => new(new(100 - remaining, 300, reset), null, null, null, null,
            now, UsageDataSource.AppServer, null, AccountKey: "a", DefaultBucketId: "codex");
        using var service = _environment.CreateService(_ => Task.FromResult(Snapshot()), Snapshot, clock: () => now);
        var settings = _environment.CreateSettings();
        var messages = new List<string>();
        using var provider = new CodexUsageDockCommandsProvider(service, settings, messages.Add, () => now);
        await service.RefreshAsync();
        settings.GetContent().OfType<FormContent>().Last().SubmitForm("""{"enableUsageAlerts":"false"}""", "{}");
        settings.GetContent().OfType<FormContent>().Last().SubmitForm("""{"enableUsageAlerts":"true"}""", "{}");
        remaining = 5;
        now = now.AddMinutes(1);
        await service.RefreshAsync();
        Assert.Empty(messages);
        remaining = 30;
        now = now.AddMinutes(1);
        await service.RefreshAsync();
        remaining = 8;
        now = now.AddMinutes(1);
        await service.RefreshAsync();
        Assert.Single(messages);
    }

    [Fact]
    public void SeparateDockBandsHaveStableRestorableIdentities()
    {
        File.WriteAllText(_environment.PathFor("settings.json"), """{"separateDockItems":"true"}""");
        using var service = _environment.CreateService();
        using var provider = new CodexUsageDockCommandsProvider(service, _environment.CreateSettings(), _ => { });
        var bands = provider.GetDockBands()!;
        Assert.Equal(3, bands.Length);
        Assert.Equal(3, bands.Select(item => item.Command.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var band in bands)
        {
            Assert.Single(Assert.IsAssignableFrom<IListPage>(band.Command).GetItems());
            Assert.Equal(band.Command.Id, provider.GetCommandItem(band.Command.Id)!.Command.Id);
        }
        Assert.Null(provider.GetCommandItem("unknown"));
        Assert.Null(provider.GetCommandItem(string.Empty));
        var combined = provider.GetCommandItem("nl.mathijs.codexusage.dock");
        Assert.Equal(3, Assert.IsAssignableFrom<IListPage>(combined!.Command).GetItems().Length);
    }
}
