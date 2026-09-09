using System.Text.Json;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class AccountUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesServerCalendarDaysAndNullableSummaryWithoutInventingValues()
    {
        var result = Parse("""
            {
              "summary": {
                "lifetimeTokens": 123456,
                "peakDailyTokens": 0,
                "longestRunningTurnSec": 3600,
                "currentStreakDays": null,
                "longestStreakDays": 12
              },
              "dailyUsageBuckets": [
                { "startDate": "2026-09-09", "tokens": 2000 },
                { "startDate": "2026-09-07", "tokens": 0 }
              ]
            }
            """);

        Assert.Equal(AccountUsageStatus.Available, result.Status);
        Assert.Equal("synthetic-hash", result.AccountKey);
        Assert.Equal(Now, result.UpdatedAt);
        Assert.Equal(123456, result.LifetimeTokens);
        Assert.Equal(0, result.PeakDailyTokens);
        Assert.Equal(3600, result.LongestRunningTurnSeconds);
        Assert.Null(result.CurrentStreakDays);
        Assert.Equal(12, result.LongestStreakDays);
        Assert.Collection(result.Days,
            day => Assert.Equal(new DailyTokenUsage(new DateOnly(2026, 9, 7), 0), day),
            day => Assert.Equal(new DailyTokenUsage(new DateOnly(2026, 9, 9), 2000), day));
    }

    [Fact]
    public void InvalidDatesNegativeTokensOverflowAndDuplicatesYieldPartialData()
    {
        var result = Parse("""
            {
              "summary": { "lifetimeTokens": -1, "peakDailyTokens": 9223372036854775808, "currentStreakDays": "10" },
              "dailyUsageBuckets": [
                { "startDate": "2026-09-09", "tokens": 100 },
                { "startDate": "2026-09-09", "tokens": 200 },
                { "startDate": "2026-02-30", "tokens": 100 },
                { "startDate": "2026-9-8", "tokens": 100 },
                { "startDate": "2026-09-08T00:00:00Z", "tokens": 100 },
                { "startDate": "2026-09-08", "tokens": -1 },
                { "startDate": "2026-09-07", "tokens": 9223372036854775808 },
                { "startDate": "2026-09-06", "tokens": "100" },
                null
              ]
            }
            """);

        Assert.Equal(AccountUsageStatus.Partial, result.Status);
        Assert.Equal(100, Assert.Single(result.Days).TotalTokens);
        Assert.Null(result.LifetimeTokens);
        Assert.Null(result.PeakDailyTokens);
        Assert.Null(result.CurrentStreakDays);
    }

    [Fact]
    public void RetainsAtMost366LatestServerDaysAndMarksTruncation()
    {
        var start = new DateOnly(2024, 1, 1);
        var daily = Enumerable.Range(0, 370).Select(index => new
        {
            startDate = start.AddDays(index).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            tokens = index,
        });

        var result = Parse(JsonSerializer.Serialize(new { dailyUsageBuckets = daily }));

        Assert.Equal(AccountUsageStatus.Partial, result.Status);
        Assert.Equal(AccountUsageParser.MaximumDays, result.Days.Count);
        Assert.Equal(start.AddDays(4), result.Days[0].Date);
        Assert.Equal(start.AddDays(369), result.Days[^1].Date);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"summary\":{\"lifetimeTokens\":null},\"dailyUsageBuckets\":null}")]
    public void MissingDataIsUnavailableRatherThanZero(string json)
    {
        var result = Parse(json);

        Assert.Equal(AccountUsageStatus.Unavailable, result.Status);
        Assert.Empty(result.Days);
        Assert.Null(result.LifetimeTokens);
        Assert.Null(result.AccountKey);
    }

    [Fact]
    public void SummaryWithoutDailyDataIsPartialAndConfirmedEmptyRowsRemainAvailable()
    {
        var summaryOnly = Parse("""{"summary":{"lifetimeTokens":0},"dailyUsageBuckets":null}""");
        var emptyDays = Parse("""{"summary":{"lifetimeTokens":null},"dailyUsageBuckets":[]}""");

        Assert.Equal(AccountUsageStatus.Partial, summaryOnly.Status);
        Assert.Equal(0, summaryOnly.LifetimeTokens);
        Assert.Equal(AccountUsageStatus.Available, emptyDays.Status);
        Assert.Empty(emptyDays.Days);
        Assert.Null(emptyDays.LifetimeTokens);
    }

    [Fact]
    public async Task ActivityIsReadBetweenMatchingAccountChecks()
    {
        var requests = new List<(string Method, int Id)>();
        var result = await CodexAppServerReader.ReadAccountUsageSequenceAsync((method, id, _) =>
        {
            requests.Add((method, id));
            return Task.FromResult(JsonDocument.Parse(method == "account/usage/read"
                ? """{"result":{"dailyUsageBuckets":[{"startDate":"2026-09-09","tokens":100}]}}"""
                : """{"result":{"accountId":"synthetic-account"}}"""));
        });

        Assert.Equal(new[] { ("account/rateLimits/read", 2), ("account/usage/read", 3), ("account/rateLimits/read", 4) }, requests);
        Assert.Equal(AccountUsageStatus.Available, result.Status);
        Assert.Equal(100, Assert.Single(result.Days).TotalTokens);
        Assert.Equal(64, result.AccountKey!.Length);
        Assert.DoesNotContain("synthetic-account", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountChangeDiscardsEveryUsageValue()
    {
        var result = await CodexAppServerReader.ReadAccountUsageSequenceAsync((method, id, _) =>
            Task.FromResult(JsonDocument.Parse(id switch
            {
                2 => """{"result":{"accountId":"synthetic-account-A"}}""",
                3 => """{"result":{"summary":{"lifetimeTokens":123},"dailyUsageBuckets":[{"startDate":"2026-09-09","tokens":100}]}}""",
                _ => """{"result":{"accountId":"synthetic-account-B"}}""",
            })));

        Assert.Equal(AccountUsageStatus.Unavailable, result.Status);
        Assert.Empty(result.Days);
        Assert.Null(result.AccountKey);
        Assert.Null(result.LifetimeTokens);
    }

    [Fact]
    public async Task MissingInitialIdentitySkipsTheAccountUsageRequest()
    {
        var count = 0;
        var result = await CodexAppServerReader.ReadAccountUsageSequenceAsync((_, _, _) =>
        {
            count++;
            return Task.FromResult(JsonDocument.Parse("""{"result":{"accountId":null}}"""));
        });

        Assert.Equal(1, count);
        Assert.Equal(AccountUsageStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData("{\"result\":{\"accountId\":null}}")]
    [InlineData("{\"error\":{\"code\":-1,\"message\":\"private details\"}}")]
    public async Task MissingFinalIdentityDiscardsActivity(string finalResponse)
    {
        var result = await CodexAppServerReader.ReadAccountUsageSequenceAsync((_, id, _) =>
            Task.FromResult(JsonDocument.Parse(id switch
            {
                2 => """{"result":{"accountId":"synthetic-account"}}""",
                3 => """{"result":{"dailyUsageBuckets":[]}}""",
                _ => finalResponse,
            })));

        Assert.Equal(AccountUsageStatus.Unavailable, result.Status);
        Assert.Null(result.AccountKey);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MethodNotFoundIsUnsupportedWithoutReadingOrExposingErrorText()
    {
        var requests = 0;
        var result = await CodexAppServerReader.ReadAccountUsageSequenceAsync((_, id, _) =>
        {
            requests++;
            return Task.FromResult(JsonDocument.Parse(id == 2
                ? """{"result":{"accountId":"synthetic-account"}}"""
                : """{"error":{"code":-32601,"message":"private path or credential"}}"""));
        });

        Assert.Equal(AccountUsageStatus.Unsupported, result.Status);
        Assert.Equal(2, requests);
        Assert.Empty(result.Days);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCanceledActivityReadDoesNotStartARequest()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CodexAppServerReader.ReadAccountUsageSequenceAsync((_, _, _) => throw new InvalidOperationException("Must not run"),
                new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task ResponseReaderIgnoresUnrelatedMalformedIdentifiers()
    {
        using var reader = new StringReader("""
            []
            {"id":"2","result":{}}
            {"id":2,"result":{}}
            """);

        var responses = await CodexAppServerReader.ReadResponsesAsync(reader, CancellationToken.None, 2);
        using var response = responses[2];
        Assert.Equal(2, response.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ResponseReaderRejectsOversizedMessagesWithABoundedError()
    {
        using var reader = new StringReader(new string('x', 1_048_577));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodexAppServerReader.ReadResponsesAsync(reader, CancellationToken.None, 2));

        Assert.Equal("Codex app-server returned an oversized response.", error.Message);
    }

    [Fact]
    public void ConfiguredSourceUsesProcessArgumentsAndAnEnvironmentVariable()
    {
        var executable = Path.Combine(Path.GetTempPath(), "synthetic cli", "codex.exe");
        var home = Path.Combine(Path.GetTempPath(), "synthetic profile & account");

        var info = CodexAppServerReader.CreateStartInfo(new CodexSourceOptions(executable, home));

        Assert.Equal(executable, info.FileName);
        Assert.Equal(new[] { "app-server", "--stdio" }, info.ArgumentList);
        Assert.Equal(home, info.Environment["CODEX_HOME"]);
        Assert.Equal(Path.GetDirectoryName(executable), info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Empty(info.Arguments);
    }

    [Fact]
    public void ActivityPageShowsAtMost30ServerDatesAndNeverAccountIdentifiers()
    {
        var start = new DateOnly(2026, 8, 1);
        var snapshot = new AccountUsageSnapshot("sensitive-account-key", Now,
            Enumerable.Range(0, 40).Select(index => new DailyTokenUsage(start.AddDays(index), index)).ToArray(),
            AccountUsageStatus.Partial, LifetimeTokens: 0);

        var text = CodexAccountActivityPage.FormatActivity(snapshot);

        Assert.Contains("Partial data", text, StringComparison.Ordinal);
        Assert.Contains("Their time zone is not specified", text, StringComparison.Ordinal);
        Assert.Contains("**Lifetime tokens:** 0", text, StringComparison.Ordinal);
        Assert.Contains("**Peak daily tokens:** Not reported", text, StringComparison.Ordinal);
        Assert.Equal(30, text.Split('\n').Count(line => line.StartsWith("| 2026-", StringComparison.Ordinal)));
        Assert.DoesNotContain("sensitive-account-key", text, StringComparison.Ordinal);
        Assert.DoesNotContain("| 2026-08-01", text, StringComparison.Ordinal);
    }

    private static AccountUsageSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return AccountUsageParser.Parse(document.RootElement, "synthetic-hash", Now);
    }
}
