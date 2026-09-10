using System.Text.Json;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class UsageAggregateStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void RetentionCanBeChangedAndIsAppliedToReloadedData()
    {
        var path = _environment.PathFor("usage-aggregates.json");
        var store = new UsageAggregateStore(path, 30, () => Now);

        Assert.True(store.Record(Point(Now.AddDays(-20)), Now, out var firstError), firstError);
        Assert.True(store.Record(Point(Now.AddDays(-6)), Now, out var secondError), secondError);
        Assert.Equal(2, store.Snapshot.Count);

        Assert.True(store.SetRetentionDays(7, Now, out var retentionError), retentionError);
        Assert.Single(store.Snapshot);
        Assert.Equal(Now.AddDays(-6), store.Snapshot[0].RecordedAt);

        var reloaded = new UsageAggregateStore(path, 7, () => Now);
        Assert.Equal(7, reloaded.RetentionDays);
        Assert.Single(reloaded.Snapshot);
        Assert.Equal(Now.AddDays(-6), reloaded.Snapshot[0].RecordedAt);
    }

    [Fact]
    public void SameBucketAndResetReplacesTheLatestPoint()
    {
        var path = _environment.PathFor("usage-aggregates.json");
        var store = new UsageAggregateStore(path, 30, () => Now);
        var reset = Now.AddDays(1);

        Assert.True(store.Record(Point(Now.AddMinutes(1), 80, reset), Now.AddMinutes(1)));
        Assert.True(store.Record(Point(Now.AddMinutes(4), 70, reset), Now.AddMinutes(4)));

        var replaced = Assert.Single(store.Snapshot);
        Assert.Equal(70, replaced.PrimaryRemainingPercent);
        Assert.Equal(Now.AddMinutes(4), replaced.RecordedAt);
    }

    [Fact]
    public void AResetChangeWithinTheSameBucketIsPreserved()
    {
        var path = _environment.PathFor("usage-aggregates.json");
        var store = new UsageAggregateStore(path, 30, () => Now);

        Assert.True(store.Record(Point(Now.AddMinutes(1), 80, Now.AddDays(1)), Now.AddMinutes(1)));
        Assert.True(store.Record(Point(Now.AddMinutes(4), 70, Now.AddDays(2)), Now.AddMinutes(4)));

        Assert.Equal(2, store.Snapshot.Count);
        Assert.Equal(
            [80d, 70d],
            store.Snapshot.Select(observation => observation.PrimaryRemainingPercent!.Value).ToArray());
    }

    [Fact]
    public void LiveFactoryRequiresKnownFreshAppServerData()
    {
        var reset = Now.AddHours(4);
        var snapshot = new CodexUsageSnapshot(
            new RateLimitWindow(20, 300, reset),
            null,
            "pro",
            null,
            null,
            Now.AddMinutes(-1),
            UsageDataSource.AppServer,
            null,
            AccountKey: "already-hashed-account-key",
            DefaultBucketId: "default");

        Assert.True(UsageAggregateStore.TryCreateLivePoint(snapshot, Now, TimeSpan.FromMinutes(1), out var point));
        Assert.NotNull(point);
        Assert.Equal(UsageDataSource.AppServer, point!.Source);
        Assert.Equal(80, point.PrimaryRemainingPercent);
        Assert.Equal(reset, point.PrimaryResetsAt);
        Assert.DoesNotContain("already-hashed-account-key", JsonSerializer.Serialize(point), StringComparison.Ordinal);

        Assert.False(UsageAggregateStore.TryCreateLivePoint(
            snapshot with { AccountKey = null }, Now, TimeSpan.FromMinutes(1), out _));
        Assert.False(UsageAggregateStore.TryCreateLivePoint(
            snapshot with { Source = UsageDataSource.LocalSession }, Now, TimeSpan.FromMinutes(1), out _));
        Assert.False(UsageAggregateStore.TryCreateLivePoint(
            snapshot with { UpdatedAt = Now.AddMinutes(-6) }, Now, TimeSpan.FromMinutes(1), out _));
        Assert.False(UsageAggregateStore.TryCreateLivePoint(
            snapshot with { UpdatedAt = Now.AddMinutes(1) }, Now, TimeSpan.FromMinutes(1), out _));
    }

    [Fact]
    public void InvalidDirectObservationIsRejectedWithGenericError()
    {
        var store = new UsageAggregateStore(_environment.PathFor("usage-aggregates.json"), 30, () => Now);
        var invalid = new UsageAggregatePoint(
            Now,
            double.NaN,
            null,
            Now.AddHours(1),
            null,
            UsageDataSource.AppServer);

        Assert.False(store.Record(invalid, Now, out var error));
        Assert.Equal("The usage observation was invalid and was not saved.", error);
        Assert.Empty(store.Snapshot);
    }

    [Fact]
    public void MalformedNullAndOversizedDocumentsAreBoundedSafely()
    {
        var path = _environment.PathFor("usage-aggregates.json");
        File.WriteAllText(path, "null");

        var malformed = new UsageAggregateStore(path, 30, () => Now);
        Assert.Empty(malformed.Snapshot);
        Assert.Contains("could not be read", malformed.StorageError, StringComparison.Ordinal);

        var points = Enumerable.Range(0, UsageAggregateStore.MaximumEntries + 5)
            .Select(index => Point(
                Now.AddMinutes(-index * 2),
                20 + index % 70,
                Now.AddMinutes(-index * 2).AddDays(1)))
            .ToArray();
        var document = new UsageAggregateStoreDocument(UsageAggregateStore.SchemaVersion, 90, points);
        File.WriteAllText(path, JsonSerializer.Serialize(document));

        var capped = new UsageAggregateStore(path, 90, () => Now);
        Assert.Equal(UsageAggregateStore.MaximumEntries, capped.Snapshot.Count);
        Assert.Equal(Now, capped.Snapshot[^1].RecordedAt);
        Assert.Equal(Now.AddMinutes(-2 * (UsageAggregateStore.MaximumEntries - 1)), capped.Snapshot[0].RecordedAt);
    }

    [Fact]
    public void InvalidEntriesAndNullItemsAreIgnoredDuringReload()
    {
        var path = _environment.PathFor("usage-aggregates.json");
        var invalidDocument = new
        {
            SchemaVersion = UsageAggregateStore.SchemaVersion,
            RetentionDays = 30,
            Observations = new object?[]
            {
                null,
                new
                {
                    RecordedAt = Now,
                    PrimaryRemainingPercent = 101d,
                    WeeklyRemainingPercent = (double?)null,
                    PrimaryResetsAt = Now.AddHours(1),
                    WeeklyResetsAt = (DateTimeOffset?)null,
                    Source = UsageDataSource.AppServer,
                },
                new
                {
                    RecordedAt = Now.AddMinutes(1),
                    PrimaryRemainingPercent = 20d,
                    WeeklyRemainingPercent = (double?)null,
                    PrimaryResetsAt = Now.AddHours(1),
                    WeeklyResetsAt = (DateTimeOffset?)null,
                    Source = UsageDataSource.LocalSession,
                },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(invalidDocument));

        var store = new UsageAggregateStore(path, 30, () => Now);

        Assert.Empty(store.Snapshot);
    }

    [Fact]
    public void ExportsAreDeterministicInvariantAndPrivacyBounded()
    {
        var path = _environment.PathFor("usage-aggregates.json");
        var store = new UsageAggregateStore(path, 30, () => Now);
        Assert.True(store.Record(
            Point(Now.AddMinutes(-1), 87.5, Now.AddHours(4), weeklyRemaining: 12.25, weeklyReset: Now.AddDays(6)),
            Now));

        var json = store.ExportJson(Now);
        var repeatedJson = store.ExportJson(Now);
        var csv = store.ExportCsv(Now);

        Assert.Equal(json, repeatedJson);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(UsageAggregateStore.SchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("UTC ISO 8601", parsed.RootElement.GetProperty("units").GetProperty("timestamps").GetString());
        Assert.Contains("percent (0-100)", json, StringComparison.Ordinal);
        Assert.Contains("87.5", json, StringComparison.Ordinal);
        Assert.Contains("12.25", json, StringComparison.Ordinal);
        Assert.Contains("2026-09-09T11:59:00.0000000Z", json, StringComparison.Ordinal);
        Assert.Contains("schemaVersion=1", csv, StringComparison.Ordinal);
        Assert.Contains("recordedAtUtc,primaryRemainingPercent,weeklyRemainingPercent", csv, StringComparison.Ordinal);
        Assert.Contains("87.5", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("87,5", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tokens\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"cost\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"account\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"session\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContextStoresAreIsolatedAndDoNotPersistContextText()
    {
        var path = _environment.PathFor("usage-aggregates.json");
        var baseStore = new UsageAggregateStore(path, 30, () => Now);
        var accountStore = baseStore.ForContext("account-id|default-category");

        Assert.True(accountStore.Record(Point(Now.AddMinutes(-1)), Now));
        Assert.Single(accountStore.Snapshot);
        Assert.Empty(baseStore.Snapshot);

        foreach (var file in Directory.EnumerateFiles(
            Path.GetDirectoryName(path)!,
            "*",
            SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("account-id", file, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("default-category", file, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("account-id", File.ReadAllText(file), StringComparison.Ordinal);
            Assert.DoesNotContain("default-category", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClearOnlyRemovesTheCurrentStore()
    {
        var path = _environment.PathFor("usage-aggregates.json");
        var baseStore = new UsageAggregateStore(path, 30, () => Now);
        var contextStore = baseStore.ForContext("known-account|default");

        Assert.True(baseStore.Record(Point(Now.AddMinutes(-2)), Now));
        Assert.True(contextStore.Record(Point(Now.AddMinutes(-1)), Now));
        Assert.True(contextStore.Clear(out var error), error);

        Assert.Single(baseStore.Snapshot);
        Assert.Empty(contextStore.Snapshot);
        Assert.Single(new UsageAggregateStore(path, 30, () => Now).Snapshot);
    }

    private static UsageAggregatePoint Point(
        DateTimeOffset recordedAt,
        double primaryRemaining = 80,
        DateTimeOffset? primaryReset = null,
        double? weeklyRemaining = null,
        DateTimeOffset? weeklyReset = null) => new(
            recordedAt,
            primaryRemaining,
            weeklyRemaining,
            primaryReset ?? recordedAt.AddHours(1),
            weeklyReset,
            UsageDataSource.AppServer);
}
