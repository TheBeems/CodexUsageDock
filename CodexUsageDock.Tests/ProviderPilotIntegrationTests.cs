using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class ProviderPilotIntegrationTests : IDisposable
{
    private readonly TestEnvironment _environment = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    public void Dispose() => _environment.Dispose();

    [Theory]
    [InlineData(1, 15, "Stale", "Available")]
    [InlineData(15, 1, "Available", "Stale")]
    public async Task ChangingOnlyTheRefreshIntervalReclassifiesClaudeAndNotifiesPresentation(
        int previousMinutes, int nextMinutes, string before, string after)
    {
        var capture = WriteCapture();
        using var service = _environment.CreateService(
            _ => Task.FromResult(CodexUsageSnapshot.Loading), () => CodexUsageSnapshot.Loading,
            clock: () => Now.AddMinutes(6));
        service.SetRefreshInterval(TimeSpan.FromMinutes(previousMinutes));
        service.ConfigureClaude(true, capture);
        await service.ClaudeRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(before, service.GetClaudeUsage().Status.ToString());
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ClaudeUpdated += (_, _) =>
        {
            if (service.GetClaudeUsage().Status.ToString() == after) updated.TrySetResult();
        };

        service.SetRefreshInterval(TimeSpan.FromMinutes(nextMinutes));
        service.ConfigureClaude(true, capture);
        await service.ClaudeRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(after, service.GetClaudeUsage().Status.ToString());
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ApplyingAProfilePersistsPathsAndLabelBeforeTheNextStart()
    {
        var executable = _environment.PathFor("codex.exe");
        File.WriteAllText(executable, string.Empty);
        var source = new CodexSourceOptions(executable, Path.GetDirectoryName(executable));
        var settings = _environment.CreateSettings();
        var changes = 0;
        settings.Changed += (_, _) => changes++;
        settings.ApplySourceProfile("Local work", source);
        var restored = _environment.CreateSettings();
        Assert.Equal(source.ExecutablePath, restored.CodexExecutablePath);
        Assert.Equal(source.HomePath, restored.CodexHomePath);
        Assert.Equal("Local work", restored.SourceLabel);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ClaudeCaptureCompletesIndependentlyOfABlockedCodexReadAndDisablingClearsIt()
    {
        var capture = WriteCapture();
        var pending = new TaskCompletionSource<CodexUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = _environment.CreateService(_ => pending.Task, () => CodexUsageSnapshot.Loading, clock: () => Now);
        var codexRead = service.RefreshAsync();
        service.ConfigureClaude(true, capture);
        await service.ClaudeRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(codexRead.IsCompleted);
        Assert.Equal(ClaudeUsageReadStatus.Available, service.GetClaudeUsage().Status);
        Assert.Equal(75, service.GetClaudeUsage().Primary!.RemainingPercent);
        var changed = JsonNode.Parse(File.ReadAllText(capture))!.AsObject();
        changed["rate_limits"]!["five_hour"]!["used_percentage"] = 50;
        File.WriteAllText(capture, changed.ToJsonString());
        Assert.Same(codexRead, service.RefreshAsync());
        await service.ClaudeRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(50, service.GetClaudeUsage().Primary!.RemainingPercent);
        Assert.False(codexRead.IsCompleted);
        service.ConfigureClaude(false, capture);
        Assert.Null(service.GetClaudeUsage().Primary);
        Assert.Contains("disabled", service.GetClaudeUsage().Message, StringComparison.Ordinal);
        pending.SetResult(CodexUsageSnapshot.Loading);
        await codexRead;
    }

    [Fact]
    public async Task ClaudeDockHasItsOwnRestorableBandAndDoesNotAlterCodexQuotas()
    {
        var capture = WriteCapture();
        File.WriteAllText(_environment.PathFor("settings.json"), new JsonObject
        { ["enableClaude"] = "true", ["claudeBridgePath"] = capture }.ToJsonString());
        var quota = new CodexUsageSnapshot(new(10, 300, Now.AddHours(4)), null, null, null, null,
            Now, UsageDataSource.AppServer, null, AccountKey: "a");
        using var service = _environment.CreateService(_ => Task.FromResult(quota), () => quota, clock: () => Now);
        using var provider = new CodexUsageDockCommandsProvider(service, _environment.CreateSettings(), _ => { }, () => Now);
        await service.RefreshAsync();
        await service.ClaudeRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(90, service.Current.Primary!.RemainingPercent);
        Assert.Equal(2, provider.GetDockBands()!.Length);
        var band = provider.GetCommandItem("nl.mathijs.codexusage.dock.claude")!;
        var items = Assert.IsAssignableFrom<IListPage>(band.Command).GetItems();
        Assert.Equal(2, items.Length);
        Assert.Equal("Claude 5h 75%", items[0].Title);
        Assert.Equal("Claude week 60%", items[1].Title);
        Assert.Contains(provider.TopLevelCommands(), item => item.Command.Id == "nl.mathijs.codexusage.table");
    }

    [Fact]
    public void TextViewKeepsUnknownZeroExpiredAndUnconfirmedStatesDistinct()
    {
        var quota = new CodexUsageSnapshot(new(100, 300, Now.AddHours(-1)), null, null, null, new(0, null),
            Now.AddHours(-2), UsageDataSource.LastConfirmed, null,
            [new("extra", "Extra | quota", new(0, 60, Now.AddHours(1)), null)], DefaultBucketId: "codex");
        var view = new UsagePresentation(quota, [], [], new([], null), LocalTokenUsageSnapshot.Unavailable, false);
        var body = CodexUsageTablePage.Format(view, Now, TimeSpan.FromMinutes(1));
        Assert.Contains("LastConfirmed", body, StringComparison.Ordinal);
        Assert.Contains("Reset passed; refresh required", body, StringComparison.Ordinal);
        Assert.Contains("Not reported", body, StringComparison.Ordinal);
        Assert.Contains("0%", body, StringComparison.Ordinal);
        Assert.Contains("100%", body, StringComparison.Ordinal);
        Assert.Contains("Extra \\| quota", body, StringComparison.Ordinal);
        Assert.DoesNotContain("![", body, StringComparison.Ordinal);
    }

    private string WriteCapture()
    {
        var path = _environment.PathFor("claude.json");
        File.WriteAllText(path, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["provider"] = "claude",
            ["observedAtUTC"] = Now.ToString("O", CultureInfo.InvariantCulture),
            ["rate_limits"] = new JsonObject
            {
                ["five_hour"] = new JsonObject { ["used_percentage"] = 25, ["resets_at"] = Now.AddHours(4).ToUnixTimeSeconds() },
                ["seven_day"] = new JsonObject { ["used_percentage"] = 40, ["resets_at"] = Now.AddDays(4).ToUnixTimeSeconds() },
            },
        }.ToJsonString());
        return path;
    }
}
