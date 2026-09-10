using System.Text.Json;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class TaskAndResetProtocolTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly string AccountKey = AccountScope("synthetic-account");
    private const string TaskId = "synthetic-thread-123";
    private const string ResetKey = "3d2054c5-d593-401d-84e4-bd8f733341cf";
    private const string AccountResponse = """{"result":{"accountId":"synthetic-account"}}""";
    private const string TaskResponse = """
        {"result":{"threadUsage":{"threadId":"synthetic-thread-123","estimatedUsageCreditsMicros":123456,"estimatedUsageUsdMicros":null,"groups":[]}}}
        """;

    [Fact]
    public void TaskUsagePreservesEstimatesAndNullableTokenClasses()
    {
        var snapshot = ParseThread("""
            {
              "threadUsage": {
                "threadId": "synthetic-thread-123",
                "estimatedUsageCreditsMicros": 1234567,
                "estimatedUsageUsdMicros": 12000,
                "groups": [{
                  "model": "synthetic-model", "reasoningEffort": "high", "speed": "fast",
                  "estimatedUsageCreditsMicros": 1234567,
                  "netNewInputTokens": 100, "cachedInputTokens": null,
                  "inputTokens": 150, "outputTokens": 20, "totalTokens": 170
                }]
              }
            }
            """);

        Assert.Equal(ThreadUsageStatus.Available, snapshot.Status);
        Assert.Equal(TaskId, snapshot.ThreadId);
        Assert.Equal(AccountKey, snapshot.AccountKey);
        Assert.Equal(1234567, snapshot.EstimatedUsageCreditsMicros);
        Assert.Equal(12000, snapshot.EstimatedUsageUsdMicros);
        var group = Assert.Single(snapshot.Groups!);
        Assert.Equal("synthetic-model", group.Model);
        Assert.Equal("high", group.ReasoningEffort);
        Assert.Equal("fast", group.Speed);
        Assert.Equal(100, group.NetNewInputTokens);
        Assert.Null(group.CachedInputTokens);
        Assert.Equal(150, group.InputTokens);
        Assert.Equal(20, group.OutputTokens);
        Assert.Equal(170, group.TotalTokens);
    }

    [Fact]
    public void InvalidTaskValuesAreUnknownAndLabelsAreSanitized()
    {
        var snapshot = ParseThread("""
            {"threadUsage": {
              "threadId":"synthetic-thread-123", "estimatedUsageCreditsMicros":-1, "estimatedUsageUsdMicros":9223372036854775808,
              "groups":[null,{}, {"model":" Model\n one ","reasoningEffort":[],"speed":"fast\tmode",
                "estimatedUsageCreditsMicros":"100","netNewInputTokens":-2,"inputTokens":0,"outputTokens":null}]
            }}
            """);

        Assert.Equal(ThreadUsageStatus.Partial, snapshot.Status);
        Assert.Null(snapshot.EstimatedUsageCreditsMicros);
        Assert.Null(snapshot.EstimatedUsageUsdMicros);
        var group = Assert.Single(snapshot.Groups!);
        Assert.Equal("Model one", group.Model);
        Assert.Null(group.ReasoningEffort);
        Assert.Equal("fast mode", group.Speed);
        Assert.Null(group.EstimatedUsageCreditsMicros);
        Assert.Null(group.NetNewInputTokens);
        Assert.Equal(0, group.InputTokens);
        Assert.Null(group.TotalTokens);
    }

    [Fact]
    public void TaskGroupsAndLabelsAreBounded()
    {
        var snapshot = ParseThread(JsonSerializer.Serialize(new
        {
            threadUsage = new
            {
                threadId = TaskId,
                estimatedUsageCreditsMicros = 0,
                groups = Enumerable.Range(0, 70).Select(index => new
                {
                    model = new string('m', 100) + index,
                    reasoningEffort = new string('e', 50),
                    speed = new string('s', 50),
                    totalTokens = index,
                }),
            },
        }));

        Assert.Equal(ThreadUsageStatus.Partial, snapshot.Status);
        Assert.Equal(64, snapshot.Groups!.Count);
        Assert.All(snapshot.Groups, group =>
        {
            Assert.Equal(80, group.Model!.Length);
            Assert.Equal(32, group.ReasoningEffort!.Length);
            Assert.Equal(32, group.Speed!.Length);
        });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"threadUsage\":null}")]
    [InlineData("{\"threadUsage\":{\"threadId\":\"foreign-thread\",\"estimatedUsageCreditsMicros\":100,\"groups\":[]}}")]
    [InlineData("{\"threadUsage\":{\"threadId\":\"synthetic-thread-123\",\"estimatedUsageCreditsMicros\":null,\"groups\":[]}}")]
    public void MissingOrForeignTaskUsageDoesNotProduceAnEstimate(string json)
    {
        var snapshot = ParseThread(json);
        Assert.Equal(ThreadUsageStatus.Unavailable, snapshot.Status);
        Assert.Null(snapshot.EstimatedUsageCreditsMicros);
        Assert.Null(snapshot.Groups);
    }

    [Fact]
    public async Task TaskReadSendsTheSelectedIdBetweenMatchingAccountChecks()
    {
        var calls = new List<string>();
        var result = await CodexAppServerReader.ReadThreadUsageSequenceAsync((method, id, parameters, _) =>
        {
            calls.Add(CodexAppServerReader.CreateRequestJson(method, id, parameters));
            return Task.FromResult(JsonDocument.Parse(method == "account/usage/read" ? TaskResponse : AccountResponse));
        }, TaskId, AccountKey);

        Assert.Equal(3, calls.Count);
        using var before = JsonDocument.Parse(calls[0]);
        using var usage = JsonDocument.Parse(calls[1]);
        using var after = JsonDocument.Parse(calls[2]);
        Assert.Equal("account/rateLimits/read", before.RootElement.GetProperty("method").GetString());
        Assert.Equal("account/usage/read", usage.RootElement.GetProperty("method").GetString());
        Assert.Equal(TaskId, usage.RootElement.GetProperty("params").GetProperty("threadId").GetString());
        Assert.Equal("account/rateLimits/read", after.RootElement.GetProperty("method").GetString());
        Assert.Equal(ThreadUsageStatus.Available, result.Status);
        Assert.Equal(123456, result.EstimatedUsageCreditsMicros);
    }

    [Fact]
    public async Task TaskReadRejectsAnAccountChangeBeforeOrAfterTheUsageRequest()
    {
        var beforeCalls = 0;
        var beforeMismatch = await CodexAppServerReader.ReadThreadUsageSequenceAsync((_, _, _, _) =>
        {
            beforeCalls++;
            return Task.FromResult(JsonDocument.Parse(AccountResponse));
        }, TaskId, AccountScope("other-account"));
        Assert.Equal(1, beforeCalls);
        Assert.Equal(ThreadUsageStatus.AccountMismatch, beforeMismatch.Status);

        var afterMismatch = await CodexAppServerReader.ReadThreadUsageSequenceAsync((_, id, _, _) =>
            Task.FromResult(JsonDocument.Parse(id switch
            {
                2 => AccountResponse,
                3 => TaskResponse,
                _ => """{"result":{"accountId":"other-account"}}""",
            })), TaskId, AccountKey);
        Assert.Equal(ThreadUsageStatus.AccountMismatch, afterMismatch.Status);
        Assert.Null(afterMismatch.EstimatedUsageCreditsMicros);
        Assert.Null(afterMismatch.AccountKey);
    }

    [Fact]
    public async Task UnsupportedTaskMethodRemainsDistinctFromUnavailableUsage()
    {
        var result = await CodexAppServerReader.ReadThreadUsageSequenceAsync((_, id, _, _) =>
            Task.FromResult(JsonDocument.Parse(id == 2 ? AccountResponse
                : """{"error":{"code":-32601,"message":"private error data"}}""")), TaskId, AccountKey);
        Assert.Equal(ThreadUsageStatus.Unsupported, result.Status);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("thread\nrequest")]
    [InlineData("thread with spaces")]
    public async Task InvalidTaskIdsDoNotSendRequests(string id)
    {
        var result = await CodexAppServerReader.ReadThreadUsageSequenceAsync((_, _, _, _) =>
            throw new InvalidOperationException("Must not send"), id, AccountKey);
        Assert.Equal(ThreadUsageStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData("reset", (int)ResetCreditOutcome.Reset)]
    [InlineData("alreadyRedeemed", (int)ResetCreditOutcome.AlreadyRedeemed)]
    [InlineData("nothingToReset", (int)ResetCreditOutcome.NothingToReset)]
    [InlineData("noCredit", (int)ResetCreditOutcome.NoCredit)]
    [InlineData("unknown-outcome", (int)ResetCreditOutcome.Ambiguous)]
    public void ResetParserPreservesTerminalServerOutcomes(string value, int expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { result = new { outcome = value } }));
        var result = ResetCreditParser.Parse(document.RootElement, Now);
        Assert.Equal((ResetCreditOutcome)expected, result.Outcome);
        Assert.Equal(Now, result.AttemptedAt);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"result\":{\"outcome\":null}}")]
    [InlineData("{\"error\":{\"code\":-32603,\"message\":\"private details\"}}")]
    [InlineData("{\"error\":{\"code\":-32601},\"result\":{\"outcome\":\"reset\"}}")]
    public void UncertainResetRepliesRemainAmbiguous(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = ResetCreditParser.Parse(document.RootElement, Now);
        Assert.Equal(ResetCreditOutcome.Ambiguous, result.Outcome);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetChecksTheExpectedAccountBeforeSendingTheCallerKey()
    {
        var requests = new List<string>();
        var result = await CodexAppServerReader.ConsumeResetCreditSequenceAsync((method, id, parameters, _) =>
        {
            requests.Add(CodexAppServerReader.CreateRequestJson(method, id, parameters));
            return Task.FromResult(JsonDocument.Parse(id == 2 ? AccountResponse : """{"result":{"outcome":"reset"}}"""));
        }, AccountKey, ResetKey);

        Assert.Equal(2, requests.Count);
        using var before = JsonDocument.Parse(requests[0]);
        using var consume = JsonDocument.Parse(requests[1]);
        Assert.Equal("account/rateLimits/read", before.RootElement.GetProperty("method").GetString());
        Assert.Equal("account/rateLimitResetCredit/consume", consume.RootElement.GetProperty("method").GetString());
        Assert.Equal(ResetKey, consume.RootElement.GetProperty("params").GetProperty("idempotencyKey").GetString());
        Assert.False(consume.RootElement.GetProperty("params").TryGetProperty("creditId", out _));
        Assert.Equal(ResetCreditOutcome.Reset, result.Outcome);
    }

    [Fact]
    public async Task ResetAccountMismatchPreventsTheMutation()
    {
        var calls = 0;
        var result = await CodexAppServerReader.ConsumeResetCreditSequenceAsync((_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(JsonDocument.Parse(AccountResponse));
        }, AccountScope("other-account"), ResetKey);
        Assert.Equal(1, calls);
        Assert.Equal(ResetCreditOutcome.AccountMismatch, result.Outcome);

        var unverified = await CodexAppServerReader.ConsumeResetCreditSequenceAsync((_, _, _, _) =>
            throw new InvalidOperationException("Must not send"), "unverified", ResetKey);
        Assert.Equal(ResetCreditOutcome.AccountMismatch, unverified.Outcome);
    }

    [Fact]
    public async Task FailedPreflightIsSafeButFailureAfterConsumeRemainsAmbiguousAndKeepsTheKey()
    {
        var unavailable = await CodexAppServerReader.ConsumeResetCreditSequenceAsync((_, _, _, _) =>
            Task.FromException<JsonDocument>(new IOException("before consume")), AccountKey, ResetKey);
        Assert.Equal(ResetCreditOutcome.Unavailable, unavailable.Outcome);

        var keys = new List<string>();
        var failed = await Attempt(throwAfterSend: true);
        Assert.Equal(ResetCreditOutcome.Ambiguous, failed.Outcome);
        Assert.Single(keys);
        var retried = await Attempt(throwAfterSend: false);
        Assert.Equal(ResetCreditOutcome.AlreadyRedeemed, retried.Outcome);
        Assert.Equal(new[] { ResetKey, ResetKey }, keys);

        Task<ResetCreditResult> Attempt(bool throwAfterSend) =>
            CodexAppServerReader.ConsumeResetCreditSequenceAsync((method, id, parameters, _) =>
            {
                if (id == 2) return Task.FromResult(JsonDocument.Parse(AccountResponse));
                using var request = JsonDocument.Parse(CodexAppServerReader.CreateRequestJson(method, id, parameters));
                keys.Add(request.RootElement.GetProperty("params").GetProperty("idempotencyKey").GetString()!);
                return throwAfterSend
                    ? Task.FromException<JsonDocument>(new IOException("after possible send"))
                    : Task.FromResult(JsonDocument.Parse("""{"result":{"outcome":"alreadyRedeemed"}}"""));
            }, AccountKey, ResetKey);
    }

    [Fact]
    public async Task CancellationAfterConsumeBeginsIsAmbiguous()
    {
        using var cancellation = new CancellationTokenSource();
        var result = await CodexAppServerReader.ConsumeResetCreditSequenceAsync((_, id, _, _) =>
        {
            if (id == 2) return Task.FromResult(JsonDocument.Parse(AccountResponse));
            cancellation.Cancel();
            return Task.FromException<JsonDocument>(new OperationCanceledException(cancellation.Token));
        }, AccountKey, ResetKey, cancellation.Token);
        Assert.Equal(ResetCreditOutcome.Ambiguous, result.Outcome);
    }

    [Fact]
    public async Task UnsupportedResetAndCancellationBeforeConsumeDoNotClaimAmbiguousConsumption()
    {
        var unsupported = await CodexAppServerReader.ConsumeResetCreditSequenceAsync((_, id, _, _) =>
            Task.FromResult(JsonDocument.Parse(id == 2 ? AccountResponse : """{"error":{"code":-32601}}""")), AccountKey, ResetKey);
        Assert.Equal(ResetCreditOutcome.Unsupported, unsupported.Outcome);

        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var canceled = await CodexAppServerReader.ConsumeResetCreditSequenceAsync((_, _, _, _) =>
        {
            calls++;
            cancellation.Cancel();
            return Task.FromResult(JsonDocument.Parse(AccountResponse));
        }, AccountKey, ResetKey, cancellation.Token);
        Assert.Equal(1, calls);
        Assert.Equal(ResetCreditOutcome.Unavailable, canceled.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("key\nwith-control")]
    [InlineData("key with space")]
    public async Task InvalidIdempotencyKeysFailBeforeAnyRequest(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CodexAppServerReader.ConsumeResetCreditSequenceAsync((_, _, _, _) =>
                throw new InvalidOperationException("Must not send"), AccountKey, key));
    }

    private static ThreadUsageSnapshot ParseThread(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ThreadUsageParser.Parse(document.RootElement, TaskId, AccountKey, Now);
    }

    private static string AccountScope(string accountId)
    {
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(new { rateLimits = new { }, accountId }));
        return CodexAppServerReader.ParseSnapshot(result.RootElement, default, Now).AccountKey!;
    }
}
