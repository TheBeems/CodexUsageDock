using System.Text.Json;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class QuotaProtocolTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RichMapOverridesOnlyTheMatchingDefaultBucket()
    {
        var snapshot = Parse("""
            {
              "rateLimits": {
                "limitId": "codex",
                "primary": { "usedPercent": 99, "windowDurationMins": 300, "resetsAt": 1788973200 }
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "limitName": "Codex",
                  "primary": { "usedPercent": 20, "windowDurationMins": 300, "resetsAt": 1788973200 },
                  "secondary": { "usedPercent": 30, "windowDurationMins": 10080, "resetsAt": 1789560000 }
                },
                "codex-spark": {
                  "limitName": "Spark",
                  "primary": { "usedPercent": 90, "windowDurationMins": 300, "resetsAt": 1788973200 },
                  "secondary": { "usedPercent": 40, "windowDurationMins": 1440, "resetsAt": 1789041600 }
                }
              }
            }
            """);

        Assert.Equal(20, snapshot.Primary!.UsedPercent);
        Assert.Equal(30, snapshot.Secondary!.UsedPercent);
        Assert.Equal("codex", snapshot.DefaultBucketId);
        Assert.Collection(
            snapshot.Buckets!,
            bucket => Assert.Equal("codex", bucket.Id),
            bucket =>
            {
                Assert.Equal("codex-spark", bucket.Id);
                Assert.Equal("Spark", bucket.Name);
                Assert.Equal(90, bucket.Primary!.UsedPercent);
                Assert.Equal(1440, bucket.Secondary!.WindowMinutes);
            });
    }

    [Fact]
    public void LegacyResponseKeepsHistoricalWindowClassification()
    {
        var snapshot = Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 35, "windowDurationMins": 10080, "resetsAt": 1789560000 },
                "secondary": { "usedPercent": 15, "windowDurationMins": 300, "resetsAt": 1788973200 }
              }
            }
            """, """{"result":{"account":{"planType":"pro"}}}""");

        Assert.Equal(15, snapshot.Primary!.UsedPercent);
        Assert.Equal(35, snapshot.Secondary!.UsedPercent);
        Assert.Equal("pro", snapshot.PlanType);
        Assert.Equal("codex", Assert.Single(snapshot.Buckets!).Id);
        Assert.Equal("codex", snapshot.DefaultBucketId);
        Assert.Equal(UsageDataSource.AppServer, snapshot.Source);
        Assert.Equal(Now, snapshot.UpdatedAt);
        Assert.Equal(Now, snapshot.LastAttemptAt);
        Assert.Null(snapshot.AccountKey);
        Assert.Null(snapshot.OrdinaryUsageAllowed);
    }

    [Fact]
    public void ExplicitLegacyBucketIdSelectsTheMatchingRichBucket()
    {
        var snapshot = Parse("""
            {
              "rateLimits": { "limitId": "codex-custom", "primary": null, "secondary": null },
              "rateLimitsByLimitId": {
                "codex": { "primary": { "usedPercent": 95, "windowDurationMins": 300, "resetsAt": 1788973200 } },
                "codex-custom": { "primary": { "usedPercent": 10, "windowDurationMins": 300, "resetsAt": 1788973200 } }
              }
            }
            """);

        Assert.Equal("codex-custom", snapshot.DefaultBucketId);
        Assert.Equal(10, snapshot.Primary!.UsedPercent);
        Assert.Equal(2, snapshot.Buckets!.Count);
    }

    [Fact]
    public void MapOnlyResponseDoesNotTreatAnotherCategoryAsTheDefaultQuota()
    {
        var snapshot = Parse("""
            {
              "rateLimitsByLimitId": {
                "codex-spark": {
                  "primary": { "usedPercent": 10, "windowDurationMins": 300, "resetsAt": 1788973200 },
                  "secondary": { "usedPercent": 20, "windowDurationMins": 10080, "resetsAt": 1789560000 }
                }
              }
            }
            """);

        Assert.Null(snapshot.Primary);
        Assert.Null(snapshot.Secondary);
        Assert.Null(snapshot.DefaultBucketId);
        Assert.Equal("codex-spark", Assert.Single(snapshot.Buckets!).Id);
    }

    [Fact]
    public void MapOnlyResponseRecognizesTheExplicitCodexDefault()
    {
        var snapshot = Parse("""
            {
              "rateLimitsByLimitId": {
                "codex": { "primary": { "usedPercent": 10, "windowDurationMins": 300, "resetsAt": 1788973200 } }
              }
            }
            """);

        Assert.Equal("codex", snapshot.DefaultBucketId);
        Assert.Equal(10, snapshot.Primary!.UsedPercent);
    }

    [Fact]
    public void UnfamiliarWindowDurationIsPreservedWithoutMislabelingIt()
    {
        var snapshot = Parse("""
            {
              "rateLimits": {
                "limitName": "Daily allowance",
                "primary": { "usedPercent": 25, "windowDurationMins": 1440, "resetsAt": 1789041600 },
                "secondary": null
              }
            }
            """);

        Assert.Null(snapshot.Primary);
        Assert.Null(snapshot.Secondary);
        var bucket = Assert.Single(snapshot.Buckets!);
        Assert.Equal("Daily allowance", bucket.Name);
        Assert.Equal(1440, bucket.Primary!.WindowMinutes);
        Assert.Equal(75, bucket.Primary.RemainingPercent);
    }

    [Fact]
    public void CreditsOnlyResponseDoesNotRequireAnActiveQuotaWindow()
    {
        var snapshot = Parse("""
            { "credits": { "hasCredits": true, "unlimited": false, "balance": "12.50" } }
            """);

        Assert.Null(snapshot.Primary);
        Assert.Null(snapshot.Secondary);
        Assert.Empty(snapshot.Buckets!);
        Assert.Equal(new CreditBalance(true, false, "12.50"), snapshot.Credits);
    }

    [Fact]
    public void ExplicitlyInactiveWindowsAndUnknownResetsRemainUnknown()
    {
        var snapshot = Parse("""
            { "rateLimits": { "primary": null, "secondary": null }, "rateLimitResetCredits": null }
            """);

        Assert.Null(snapshot.Primary);
        Assert.Null(snapshot.Secondary);
        Assert.Null(snapshot.ResetCredits);
        Assert.Single(snapshot.Buckets!);
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"availableCount\":null}", null)]
    [InlineData("{\"availableCount\":\"0\"}", null)]
    [InlineData("{\"availableCount\":-1}", null)]
    [InlineData("{\"availableCount\":0,\"credits\":null}", 0)]
    [InlineData("{\"availableCount\":2,\"credits\":[]}", 2)]
    public void ResetCountDistinguishesUnavailableFromConfirmedZero(string resetJson, int? expectedCount)
    {
        var snapshot = Parse("{\"rateLimits\":{},\"rateLimitResetCredits\":" + resetJson + "}");

        Assert.Equal(expectedCount, snapshot.ResetCredits?.AvailableCount);
    }

    [Fact]
    public void InvalidOptionalEntriesDoNotDiscardValidBuckets()
    {
        var snapshot = Parse("""
            {
              "rateLimits": { "planType": "pro" },
              "rateLimitsByLimitId": {
                "invalid-array": [],
                "invalid-null": null,
                "invalid\nkey": {},
                "codex": {
                  "limitName": ["ignored"],
                  "primary": { "usedPercent": "25", "windowDurationMins": 300, "resetsAt": 1788973200 },
                  "secondary": { "usedPercent": 30, "windowDurationMins": 10080, "resetsAt": 1789560000 }
                },
                "codex-spark": { "limitName": " Spark\n allowance\t " }
              },
              "ordinaryUsageAllowed": "true",
              "rateLimitResetCredits": []
            }
            """, """{"result":{"account":[]}}""");

        Assert.Null(snapshot.Primary);
        Assert.Equal(30, snapshot.Secondary!.UsedPercent);
        Assert.Null(snapshot.Buckets![0].Name);
        Assert.Equal("Spark allowance", snapshot.Buckets[1].Name);
        Assert.Equal(2, snapshot.Buckets.Count);
        Assert.Null(snapshot.OrdinaryUsageAllowed);
        Assert.Null(snapshot.ResetCredits);
        Assert.Null(snapshot.PlanType);
    }

    [Fact]
    public void BucketsAreBoundedDeduplicatedAndKeepTheDefaultEvenWhenItIsLast()
    {
        var map = Enumerable.Range(0, 40).Select(index => $"\"quota-{index}\":{{}}");
        var snapshot = Parse("{\"rateLimitsByLimitId\":{" + string.Join(',', map)
            + ",\"quota-0\":{},\"codex\":{\"limitName\":\"Default\"}}}");

        Assert.Equal(32, snapshot.Buckets!.Count);
        Assert.Equal("codex", snapshot.Buckets[0].Id);
        Assert.Equal(32, snapshot.Buckets.Select(bucket => bucket.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("codex", snapshot.DefaultBucketId);
    }

    [Fact]
    public void UnsafeOrOversizedIdsAreSkippedAndLabelsAreBounded()
    {
        var oversizedId = new string('a', 129);
        var oversizedLabel = new string('n', 200);
        var snapshot = Parse(JsonSerializer.Serialize(new
        {
            rateLimitsByLimitId = new Dictionary<string, object>
            {
                [oversizedId] = new { },
                ["bad id"] = new { },
                ["codex"] = new { limitName = oversizedLabel },
            },
        }));

        var bucket = Assert.Single(snapshot.Buckets!);
        Assert.Equal("codex", bucket.Id);
        Assert.Equal(80, bucket.Name!.Length);
    }

    [Fact]
    public void AccountScopeHashesOnlyTheUsageResponseIdentity()
    {
        var first = Parse("""{"rateLimits":{},"accountId":"synthetic-account-A"}""",
            """{"result":{"account":{"email":"synthetic@example.invalid","planType":"pro"}}}""");
        var same = Parse("""{"rateLimits":{},"accountId":"synthetic-account-A"}""");
        var other = Parse("""{"rateLimits":{},"accountId":"synthetic-account-B"}""");
        var unverified = Parse("""{"rateLimits":{}}""",
            """{"result":{"account":{"email":"synthetic@example.invalid","accountId":"synthetic-account-A"}}}""");

        Assert.NotNull(first.AccountKey);
        Assert.Equal(64, first.AccountKey.Length);
        Assert.Equal(first.AccountKey, same.AccountKey);
        Assert.NotEqual(first.AccountKey, other.AccountKey);
        Assert.Null(unverified.AccountKey);
        var serialized = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("synthetic-account", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic@example.invalid", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    [InlineData("\"synthetic\\naccount\"")]
    public void InvalidIdentityDoesNotCreateAnAccountScope(string accountJson)
    {
        var snapshot = Parse("{\"rateLimits\":{},\"accountId\":" + accountJson + "}");

        Assert.Null(snapshot.AccountKey);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    [InlineData("\"false\"", null)]
    public void BackendPermissionIsPreservedWithoutInferringRecovery(string allowedJson, bool? expected)
    {
        var snapshot = Parse("{\"rateLimits\":{},\"ordinaryUsageAllowed\":" + allowedJson + "}");

        Assert.Equal(expected, snapshot.OrdinaryUsageAllowed);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"rateLimits\":[]}")]
    public void UnusableResponseFailsWithoutExposingPayloads(string json)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.StartsWith("Codex app-server did not return", error.Message, StringComparison.Ordinal);
    }

    private static CodexUsageSnapshot Parse(string rateJson, string accountJson = "{}")
    {
        using var rates = JsonDocument.Parse(rateJson);
        using var account = JsonDocument.Parse(accountJson);
        return CodexAppServerReader.ParseSnapshot(rates.RootElement, account.RootElement, Now);
    }
}
