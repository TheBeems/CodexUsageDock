using System.Text.Json;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class UsagePreferenceTests : IDisposable
{
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void NewPreferencesUseSafeDefaults()
    {
        var settings = _environment.CreateSettings();

        Assert.False(settings.EnableUsageAlerts);
        Assert.False(settings.CompactDock);
        Assert.False(settings.SeparateDockItems);
        Assert.True(settings.ShowAccountActivity);
    }

    [Fact]
    public void UsagePreferencesPersistAcrossNewPages()
    {
        var first = _environment.CreateSettings();
        SubmitSettings(first, new Dictionary<string, string>
        {
            ["enableUsageAlerts"] = "true",
            ["compactDock"] = "true",
            ["separateDockItems"] = "true",
            ["showAccountActivity"] = "false",
        });

        Assert.True(first.EnableUsageAlerts);
        Assert.True(first.CompactDock);
        Assert.True(first.SeparateDockItems);
        Assert.False(first.ShowAccountActivity);

        var restarted = _environment.CreateSettings();

        Assert.True(restarted.EnableUsageAlerts);
        Assert.True(restarted.CompactDock);
        Assert.True(restarted.SeparateDockItems);
        Assert.False(restarted.ShowAccountActivity);
    }

    [Fact]
    public void SettingsLoadKeepsValidValuesAndRejectsInvalidChoices()
    {
        File.WriteAllText(_environment.PathFor("settings.json"), JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["enableUsageAlerts"] = "true",
                ["compactDock"] = "true",
                ["separateDockItems"] = "not-a-boolean",
                ["showAccountActivity"] = "false",
            }));

        var settings = _environment.CreateSettings();

        Assert.True(settings.EnableUsageAlerts);
        Assert.True(settings.CompactDock);
        Assert.False(settings.SeparateDockItems);
        Assert.False(settings.ShowAccountActivity);
    }

    [Theory]
    [InlineData("FiveHour", 47, false, "5h 47%")]
    [InlineData("Weekly", 86, false, "Week 86%")]
    [InlineData("FiveHour", 47, true, "5h47%")]
    [InlineData("Weekly", 86, true, "W86%")]
    public void FormatQuotaTitleSupportsFullAndCompactModes(
        string kindName,
        double remainingPercent,
        bool compact,
        string expected)
    {
        var kind = Enum.Parse<UsageDockItemKind>(kindName);
        Assert.Equal(expected, UsageDockItem.FormatQuotaTitle(kind, remainingPercent, compact));
    }

    [Fact]
    public async Task NonCompactModeKeepsResetTimeAndFreshnessWarning()
    {
        var now = DateTimeOffset.Now;
        var snapshot = new CodexUsageSnapshot(
            new RateLimitWindow(53, 300, now.AddHours(4)),
            null,
            "pro",
            null,
            null,
            now.AddMinutes(-10),
            UsageDataSource.AppServer,
            null);

        using var service = _environment.CreateService(_ => Task.FromResult(snapshot), () => snapshot);
        var settings = _environment.CreateSettings();
        using var details = new CodexUsageDockPage(service, settings);
        using var item = new UsageDockItem(service, UsageDockItemKind.FiveHour, details, settings);

        await service.RefreshAsync();

        Assert.Equal("5h 47%", item.Title);
        Assert.Contains("Stale", item.Subtitle, StringComparison.Ordinal);
        Assert.Contains("reset", item.Subtitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompactModeUsesShortTitleAndOmitsResetTimeButKeepsFreshnessWarning()
    {
        var now = DateTimeOffset.Now;
        var snapshot = new CodexUsageSnapshot(
            new RateLimitWindow(53, 300, now.AddHours(4)),
            null,
            "pro",
            null,
            null,
            now.AddMinutes(-10),
            UsageDataSource.AppServer,
            null);

        using var service = _environment.CreateService(_ => Task.FromResult(snapshot), () => snapshot);
        var settings = _environment.CreateSettings();
        SubmitSettings(settings, new Dictionary<string, string> { ["compactDock"] = "true" });
        using var details = new CodexUsageDockPage(service, settings);
        using var item = new UsageDockItem(service, UsageDockItemKind.FiveHour, details, settings);

        await service.RefreshAsync();

        Assert.Equal("5h47%", item.Title);
        Assert.Contains("Stale", item.Subtitle, StringComparison.Ordinal);
        Assert.DoesNotContain("reset", item.Subtitle, StringComparison.OrdinalIgnoreCase);
    }

    private static void SubmitSettings(CodexUsageDockSettingsPage page, IReadOnlyDictionary<string, string> values)
    {
        var payload = new Dictionary<string, string>
        {
            ["showFiveHourLimit"] = "true",
            ["showWeeklyLimit"] = "true",
            ["showResetsAndCredits"] = "true",
            ["showResetTime"] = "true",
            ["useAdaptiveWeeklyForecast"] = "true",
            ["refreshInterval"] = "1",
        };

        foreach (var pair in values)
        {
            payload[pair.Key] = pair.Value;
        }

        var form = page.GetContent().OfType<FormContent>().Last();
        form.SubmitForm(JsonSerializer.Serialize(payload), "{}");
    }
}
