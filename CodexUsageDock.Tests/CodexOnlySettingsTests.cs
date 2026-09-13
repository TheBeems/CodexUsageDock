using System.Text.Json;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class CodexOnlySettingsTests : IDisposable
{
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    [Theory]
    [InlineData("codexExecutablePath")]
    [InlineData("codexHomePath")]
    [InlineData("sourceLabel")]
    [InlineData("enableClaude")]
    [InlineData("claudeBridgePath")]
    public void SettingsDoNotOfferRemovedSourceControls(string key)
    {
        var settings = _environment.CreateSettings();
        var content = string.Join("\n", settings.GetContent().OfType<FormContent>()
            .Select(form => form.TemplateJson + form.DataJson));

        Assert.DoesNotContain(key, content, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingLegacySettingsKeepsCodexChoicesAndDropsRemovedPreferences()
    {
        var legacy = LegacySettings();
        File.WriteAllText(_environment.PathFor("settings.json"), JsonSerializer.Serialize(legacy));
        var settings = _environment.CreateSettings();

        Assert.False(settings.ShowFiveHourLimit);
        Assert.True(settings.CompactDock);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.RefreshInterval);
        Assert.Null(settings.StatusMessage);

        legacy["refreshInterval"] = "15";
        settings.GetContent().OfType<FormContent>().Last().SubmitForm(JsonSerializer.Serialize(legacy), "{}");

        using var saved = JsonDocument.Parse(File.ReadAllText(_environment.PathFor("settings.json")));
        foreach (var key in new[] { "codexExecutablePath", "codexHomePath", "sourceLabel", "enableClaude", "claudeBridgePath" })
        {
            Assert.False(saved.RootElement.TryGetProperty(key, out _), key);
        }

        var restarted = _environment.CreateSettings();
        Assert.False(restarted.ShowFiveHourLimit);
        Assert.True(restarted.CompactDock);
        Assert.Equal(TimeSpan.FromMinutes(15), restarted.RefreshInterval);
        Assert.Null(restarted.StatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySourcePreferencesDoNotBlockCodexOrRestoreRemovedCommands(bool separate)
    {
        var legacy = LegacySettings();
        legacy["separateDockItems"] = separate ? "true" : "false";
        File.WriteAllText(_environment.PathFor("settings.json"), JsonSerializer.Serialize(legacy));
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var quota = new CodexUsageSnapshot(new(25, 300, now.AddHours(4)), new(40, 10080, now.AddDays(3)),
            null, null, null, now, UsageDataSource.AppServer, null, AccountKey: "account-a");
        using var service = _environment.CreateService(_ => Task.FromResult(quota), () => CodexUsageSnapshot.Loading,
            clock: () => now);
        var settings = _environment.CreateSettings();
        using var provider = new CodexUsageDockCommandsProvider(service, settings, _ => { }, () => now);

        await service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UsageDataSource.AppServer, service.Current.Source);
        Assert.Equal(quota.AccountKey, service.Current.AccountKey);
        Assert.Equal(quota.Primary, service.Current.Primary);
        Assert.Equal(quota.Secondary, service.Current.Secondary);
        Assert.Null(settings.StatusMessage);
        var removedIds = new[] { "nl.mathijs.codexusage.dock.claude", "nl.mathijs.codexusage.claude", "nl.mathijs.codexusage.profiles" };
        foreach (var id in removedIds)
        {
            Assert.Null(provider.GetCommandItem(id));
            Assert.DoesNotContain(provider.TopLevelCommands(), item => item.Command.Id == id);
        }

        var expectedIds = separate
            ? new[] { "nl.mathijs.codexusage.dock.weekly", "nl.mathijs.codexusage.dock.credits" }
            : ["nl.mathijs.codexusage.dock"];
        Assert.Equal(expectedIds, provider.GetDockBands()!.Select(item => item.Command.Id));
        Assert.Contains(provider.TopLevelCommands(), item => item.Command.Id == "nl.mathijs.codexusage.table");
        Assert.All(provider.GetDockBands()!, item => Assert.IsAssignableFrom<IListPage>(item.Command));
    }

    private static Dictionary<string, string> LegacySettings() => new()
    {
        ["showFiveHourLimit"] = "false",
        ["compactDock"] = "true",
        ["refreshInterval"] = "5",
        ["codexExecutablePath"] = "removed-invalid-executable-path",
        ["codexHomePath"] = "removed-invalid-home-path",
        ["sourceLabel"] = "Old source",
        ["enableClaude"] = "true",
        ["claudeBridgePath"] = "removed-invalid-capture-path",
    };
}
