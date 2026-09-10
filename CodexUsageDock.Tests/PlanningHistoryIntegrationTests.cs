using System.Text.Json;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class PlanningHistoryIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void PlanningPreferencesRoundTripAndExposeSafeDefaults()
    {
        var first = _environment.CreateSettings();

        SubmitSettings(first, new Dictionary<string, string>
        {
            ["historyRetentionDays"] = "90",
            ["workdayEnd"] = "18:30",
            ["remainingWorkdays"] = "5",
        });

        Assert.Equal(90, first.HistoryRetentionDays);
        Assert.Equal(new TimeOnly(18, 30), first.WorkdayEnd);
        Assert.Equal(5, first.RemainingWorkdays);

        var restarted = _environment.CreateSettings();
        Assert.Equal(90, restarted.HistoryRetentionDays);
        Assert.Equal(new TimeOnly(18, 30), restarted.WorkdayEnd);
        Assert.Equal(5, restarted.RemainingWorkdays);

        File.Delete(_environment.PathFor("settings.json"));
        var defaults = _environment.CreateSettings();
        Assert.Equal(0, defaults.HistoryRetentionDays);
        Assert.Equal(new TimeOnly(17, 0), defaults.WorkdayEnd);
        Assert.Equal(1, defaults.RemainingWorkdays);
    }

    [Fact]
    public void InvalidPlanningPreferencesUseSafeDefaults()
    {
        File.WriteAllText(_environment.PathFor("settings.json"), JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["historyRetentionDays"] = "365",
                ["workdayEnd"] = "25:61",
                ["remainingWorkdays"] = "0",
            }));

        var settings = _environment.CreateSettings();

        Assert.Equal(0, settings.HistoryRetentionDays);
        Assert.Equal(new TimeOnly(17, 0), settings.WorkdayEnd);
        Assert.Equal(1, settings.RemainingWorkdays);
    }

    [Fact]
    public void AggregateRetentionIsOptInAndPausingDoesNotAppend()
    {
        var now = Now;
        using var service = CreateService(() => now);

        service.RecordHistory(Snapshot("account-a", "default", 80, now), now);
        var paused = service.GetAggregateHistory();
        Assert.Equal(0, paused.RetentionDays);
        Assert.True(paused.Identified);
        Assert.Empty(paused.Points);

        service.SetAggregateRetentionDays(30);
        now = Now.AddMinutes(6);
        service.RecordHistory(Snapshot("account-a", "default", 70, now), now);
        var enabled = service.GetAggregateHistory();
        Assert.Equal(30, enabled.RetentionDays);
        Assert.Equal(70, Assert.Single(enabled.Points).PrimaryRemainingPercent);

        service.SetAggregateRetentionDays(0);
        now = Now.AddMinutes(12);
        service.RecordHistory(Snapshot("account-a", "default", 60, now), now);
        var pausedAgain = service.GetAggregateHistory();
        Assert.Equal(0, pausedAgain.RetentionDays);
        var retained = Assert.Single(pausedAgain.Points);
        Assert.Equal(70, retained.PrimaryRemainingPercent);
    }

    [Fact]
    public void AggregateHistoryIsSeparatedByAccountAndCategory()
    {
        var now = Now;
        using var service = CreateService(() => now);
        service.SetAggregateRetentionDays(30);

        service.RecordHistory(Snapshot("account-a", "default", 80, now), now);
        Assert.Equal(80, Assert.Single(service.GetAggregateHistory().Points).PrimaryRemainingPercent);

        now = Now.AddMinutes(6);
        service.RecordHistory(Snapshot("account-b", "default", 20, now), now);
        Assert.Equal(20, Assert.Single(service.GetAggregateHistory().Points).PrimaryRemainingPercent);

        now = Now.AddMinutes(12);
        service.RecordHistory(Snapshot("account-a", "review", 50, now), now);
        Assert.Equal(50, Assert.Single(service.GetAggregateHistory().Points).PrimaryRemainingPercent);

        // Revisit the original scope with its original measurement. The store
        // must reload account-a/default rather than exposing the review or b data.
        service.RecordHistory(Snapshot("account-a", "default", 80, Now), Now);
        var restored = service.GetAggregateHistory();
        Assert.Equal(80, Assert.Single(restored.Points).PrimaryRemainingPercent);
        Assert.True(restored.Identified);
    }

    [Fact]
    public void ExplicitExportIsScopedAndContainsNoIdentifiers()
    {
        var now = Now;
        using var service = CreateService(() => now);
        service.SetAggregateRetentionDays(30);

        service.RecordHistory(Snapshot("account-a", "default", 80, now), now);
        now = Now.AddMinutes(6);
        service.RecordHistory(Snapshot("account-b", "default", 20, now), now);

        var result = service.ExportAggregateHistory(csv: false);
        const string prefix = "Export saved to ";
        Assert.StartsWith(prefix, result, StringComparison.Ordinal);
        var exportPath = result[prefix.Length..];
        Assert.True(File.Exists(exportPath));

        var content = File.ReadAllText(exportPath);
        using var document = JsonDocument.Parse(content);
        var observations = document.RootElement.GetProperty("observations");
        var observation = Assert.Single(observations.EnumerateArray());
        Assert.Equal(20, observation.GetProperty("primaryRemainingPercent").GetDouble());
        Assert.DoesNotContain("account-a", content, StringComparison.Ordinal);
        Assert.DoesNotContain("account-b", content, StringComparison.Ordinal);
        Assert.DoesNotContain("default", content, StringComparison.Ordinal);
        Assert.DoesNotContain("accountKey", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bucketId", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContextGuardsKeepTheCurrentAccountHistoryAndBlockStaleActions()
    {
        var now = Now;
        using var service = CreateService(() => now);
        service.SetAggregateRetentionDays(30);

        service.RecordHistory(Snapshot("account-a", "default", 80, now), now);
        var accountA = service.GetAggregateHistory();
        Assert.NotNull(accountA.Context);

        now = Now.AddMinutes(6);
        service.RecordHistory(Snapshot("account-b", "default", 20, now), now);
        var accountB = service.GetAggregateHistory();
        Assert.NotEqual(accountA.Context, accountB.Context);
        Assert.Equal(20, Assert.Single(accountB.Points).PrimaryRemainingPercent);

        Assert.False(service.ClearAggregateHistory(accountA.Context));
        var exportDirectory = Path.Combine(
            Path.GetDirectoryName(_environment.PathFor("aggregates.json"))!,
            "exports");
        Assert.False(Directory.Exists(exportDirectory));

        var staleExport = service.ExportAggregateHistory(csv: false, expectedContext: accountA.Context);
        Assert.Contains("changed", staleExport, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(exportDirectory));

        var preserved = service.GetAggregateHistory();
        Assert.Equal(accountB.Context, preserved.Context);
        Assert.Equal(20, Assert.Single(preserved.Points).PrimaryRemainingPercent);
    }

    private CodexUsageService CreateService(Func<DateTimeOffset> clock) => _environment.CreateService(
        _ => Task.FromResult(CodexUsageSnapshot.Loading),
        () => CodexUsageSnapshot.Loading,
        clock: clock);

    private static CodexUsageSnapshot Snapshot(
        string account,
        string category,
        double remaining,
        DateTimeOffset updatedAt) => new(
        new RateLimitWindow(100 - remaining, 300, updatedAt.AddHours(4)),
        new RateLimitWindow(100 - remaining, 10080, updatedAt.AddDays(6)),
        "pro",
        null,
        null,
        updatedAt,
        UsageDataSource.AppServer,
        null,
        AccountKey: account,
        DefaultBucketId: category);

    private static void SubmitSettings(
        CodexUsageDockSettingsPage page,
        IReadOnlyDictionary<string, string> values)
    {
        var payload = new Dictionary<string, string>
        {
            ["historyRetentionDays"] = "0",
            ["workdayEnd"] = "17:00",
            ["remainingWorkdays"] = "1",
        };

        foreach (var pair in values)
        {
            payload[pair.Key] = pair.Value;
        }

        var form = page.GetContent().OfType<FormContent>().Last();
        form.SubmitForm(JsonSerializer.Serialize(payload), "{}");
    }
}
