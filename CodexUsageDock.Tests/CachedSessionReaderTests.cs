using System.Text;
using System.Text.Json;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class CachedSessionReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly TestEnvironment _environment = new();

    private string HomePath => _environment.PathFor("cached-codex-home");

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void SelectionUsesEventTimeAcrossActiveAndArchivedFilesAndClearsInactiveWindows()
    {
        WriteSession("rollout-active.jsonl", QuotaLine(Now.AddHours(-2), 70, 40));
        var archived = WriteSession("rollout-archived.jsonl",
            QuotaLine(Now.AddMinutes(-10), null, null) + QuotaLine(Now.AddHours(-3), 80, 60), archived: true);
        File.SetLastWriteTimeUtc(archived, Now.AddDays(-10).UtcDateTime);
        var reader = CreateReader();

        var result = reader.ReadLatest();

        Assert.Equal(Now.AddMinutes(-10), result.UpdatedAt);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
        Assert.Null(result.AccountKey);
        Assert.Equal(UsageDataSource.LocalSession, result.Source);
        Assert.True(reader.LastScanComplete);
        Assert.Equal(2, reader.CachedFileCount);
    }

    [Fact]
    public void UnchangedCachedFilesReadZeroContentBytesEvenWhenTheirContentCannotBeOpened()
    {
        var path = WriteSession("rollout-one.jsonl", QuotaLine(Now, 25));
        var reader = CreateReader();
        var first = reader.ReadLatest();
        Assert.Equal(new FileInfo(path).Length, reader.BytesReadLastScan);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var unchanged = reader.ReadLatest();

        Assert.Equal(first, unchanged);
        Assert.Equal(0, reader.BytesReadLastScan);
        Assert.True(reader.LastScanComplete);
    }

    [Fact]
    public void AppendingReadsOnlyTheNewBytesAndPreservesTheMeasurementTimestamp()
    {
        var path = WriteSession("rollout-one.jsonl", QuotaLine(Now.AddHours(-1), 25));
        var reader = CreateReader();
        reader.ReadLatest();
        var appended = QuotaLine(Now.AddMinutes(-5), 45);
        File.AppendAllText(path, appended);

        var result = reader.ReadLatest();

        Assert.Equal(Encoding.UTF8.GetByteCount(appended), reader.BytesReadLastScan);
        Assert.Equal(Now.AddMinutes(-5), result.UpdatedAt);
        Assert.Equal(45, result.Primary!.UsedPercent);
        Assert.True(reader.LastScanComplete);
        reader.ReadLatest();
        Assert.Equal(0, reader.BytesReadLastScan);
    }

    [Fact]
    public void ACompleteJsonObjectWithoutItsNewlineIsNotPublishedUntilTheLineCompletes()
    {
        var path = WriteSession("rollout-one.jsonl", QuotaLine(Now.AddHours(-1), 25));
        var reader = CreateReader();
        reader.ReadLatest();
        var appended = QuotaLine(Now, 45).TrimEnd('\n');
        File.AppendAllText(path, appended);

        var unfinished = reader.ReadLatest();

        Assert.Equal(25, unfinished.Primary!.UsedPercent);
        Assert.False(reader.LastScanComplete);
        Assert.Contains("scan incomplete", unfinished.Error, StringComparison.Ordinal);
        reader.ReadLatest();
        Assert.Equal(0, reader.BytesReadLastScan);
        File.AppendAllText(path, "\n");

        var completed = reader.ReadLatest();

        Assert.Equal(1, reader.BytesReadLastScan);
        Assert.Equal(45, completed.Primary!.UsedPercent);
        Assert.True(reader.LastScanComplete);
        Assert.Null(completed.Error);
    }

    [Fact]
    public void Utf8CharactersSplitAcrossAppendsAreParsedOnlyAfterTheRecordCompletes()
    {
        var path = WriteSession("rollout-one.jsonl", QuotaLine(Now.AddHours(-1), 25));
        var reader = CreateReader();
        reader.ReadLatest();
        var appended = Encoding.UTF8.GetBytes(QuotaLine(Now, 40).Replace("\"pro\"", "\"café\"", StringComparison.Ordinal));
        var split = Array.IndexOf(appended, (byte)0xC3) + 1;
        Assert.True(split > 0);
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write)) stream.Write(appended.AsSpan(0, split));
        Assert.Equal(25, reader.ReadLatest().Primary!.UsedPercent);
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write)) stream.Write(appended.AsSpan(split));

        var completed = reader.ReadLatest();

        Assert.Equal("café", completed.PlanType);
        Assert.Equal(appended.Length - split, reader.BytesReadLastScan);
        Assert.True(reader.LastScanComplete);
    }

    [Fact]
    public void TruncatingAFileInvalidatesItsPreviousMeasurementAndPartialLine()
    {
        var path = WriteSession("rollout-one.jsonl", QuotaLine(Now, 95) + new string('x', 4096));
        var reader = CreateReader();
        Assert.Equal(95, reader.ReadLatest().Primary!.UsedPercent);
        var replacement = QuotaLine(Now.AddHours(-1), 10);
        File.WriteAllText(path, replacement);

        var result = reader.ReadLatest();

        Assert.Equal(10, result.Primary!.UsedPercent);
        Assert.Equal(Now.AddHours(-1), result.UpdatedAt);
        Assert.Equal(Encoding.UTF8.GetByteCount(replacement), reader.BytesReadLastScan);
        Assert.True(reader.LastScanComplete);
    }

    [Fact]
    public void RewritingTheSameLengthWithANewModificationTimeInvalidatesTheCheckpoint()
    {
        var initial = QuotaLine(Now, 95);
        var replacement = QuotaLine(Now, 10);
        Assert.Equal(initial.Length, replacement.Length);
        var path = WriteSession("rollout-one.jsonl", initial);
        var reader = CreateReader();
        reader.ReadLatest();
        File.WriteAllText(path, replacement);
        File.SetLastWriteTimeUtc(path, Now.AddMinutes(1).UtcDateTime);

        var result = reader.ReadLatest();

        Assert.Equal(10, result.Primary!.UsedPercent);
        Assert.Equal(Encoding.UTF8.GetByteCount(replacement), reader.BytesReadLastScan);
    }

    [Fact]
    public void AChangedCreationTimeInvalidatesAReplacedFileEvenWhenItGrew()
    {
        var path = WriteSession("rollout-one.jsonl", QuotaLine(Now, 95));
        File.SetCreationTimeUtc(path, Now.AddDays(-2).UtcDateTime);
        var reader = CreateReader();
        reader.ReadLatest();
        var replacement = QuotaLine(Now.AddHours(-1), 10) + "{\"message\":\"replacement\"}\n";
        File.WriteAllText(path, replacement);
        File.SetCreationTimeUtc(path, Now.AddDays(-1).UtcDateTime);

        var result = reader.ReadLatest();

        Assert.Equal(10, result.Primary!.UsedPercent);
        Assert.Equal(Now.AddHours(-1), result.UpdatedAt);
        Assert.Equal(Encoding.UTF8.GetByteCount(replacement), reader.BytesReadLastScan);
    }

    [Fact]
    public void DeletingTheLatestFileFallsBackToAnotherCachedMeasurementWithoutRereadingIt()
    {
        WriteSession("rollout-older.jsonl", QuotaLine(Now.AddHours(-1), 25));
        var newest = WriteSession("rollout-newest.jsonl", QuotaLine(Now, 45));
        var reader = CreateReader();
        Assert.Equal(45, reader.ReadLatest().Primary!.UsedPercent);
        File.Delete(newest);

        var result = reader.ReadLatest();

        Assert.Equal(25, result.Primary!.UsedPercent);
        Assert.Equal(0, reader.BytesReadLastScan);
        Assert.Equal(1, reader.CachedFileCount);
        Assert.True(reader.LastScanComplete);
    }

    [Fact]
    public void UnreadableFilesAreRetriedAndMalformedOrFutureRecordsDoNotReplaceValidUsage()
    {
        WriteSession("rollout-readable.jsonl", QuotaLine(Now.AddHours(-1), 25)
            + "[\"rate_limits\"]\n{\"rate_limits\":invalid}\n" + QuotaLine(Now.AddHours(1), 99));
        var locked = WriteSession("rollout-locked.jsonl", QuotaLine(Now, 45));
        var reader = CreateReader();
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = reader.ReadLatest();
            Assert.Equal(25, result.Primary!.UsedPercent);
            Assert.False(reader.LastScanComplete);
            Assert.Contains("scan incomplete", result.Error, StringComparison.Ordinal);
        }

        var recovered = reader.ReadLatest();

        Assert.Equal(45, recovered.Primary!.UsedPercent);
        Assert.Equal(new FileInfo(locked).Length, reader.BytesReadLastScan);
        Assert.True(reader.LastScanComplete);
    }

    [Fact]
    public void InitialBudgetExhaustionReturnsObservedUsageAndContinuesFromItsCheckpoint()
    {
        const int budget = 512;
        var path = WriteSession("rollout-large.jsonl", QuotaLine(Now.AddHours(-1), 25)
            + new string('x', 4096) + "\n" + QuotaLine(Now, 45));
        var reader = CreateReader(maxBytesPerScan: budget, maxLineBytes: 1024, fileReadQuantum: 256);

        var result = reader.ReadLatest();
        long totalBytes = reader.BytesReadLastScan;

        Assert.Equal(25, result.Primary!.UsedPercent);
        Assert.Equal(budget, reader.BytesReadLastScan);
        Assert.False(reader.LastScanComplete);
        Assert.Contains("scan incomplete", result.Error, StringComparison.Ordinal);
        for (var scan = 0; scan < 20 && !reader.LastScanComplete; scan++)
        {
            result = reader.ReadLatest();
            Assert.InRange(reader.BytesReadLastScan, 0, budget);
            totalBytes += reader.BytesReadLastScan;
        }
        Assert.True(reader.LastScanComplete);
        Assert.Equal(45, result.Primary!.UsedPercent);
        Assert.Equal(new FileInfo(path).Length, totalBytes);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ANewFileReceivesAReadTurnWhileAnOlderLargeFileIsStillPending()
    {
        WriteSession("rollout-large.jsonl", QuotaLine(Now.AddHours(-1), 25) + new string('x', 50_000) + "\n");
        var reader = CreateReader(maxBytesPerScan: 1024, fileReadQuantum: 512);
        Assert.Equal(25, reader.ReadLatest().Primary!.UsedPercent);
        WriteSession("rollout-newest.jsonl", QuotaLine(Now, 45));

        var result = reader.ReadLatest();

        Assert.Equal(45, result.Primary!.UsedPercent);
        Assert.False(reader.LastScanComplete);
        Assert.InRange(reader.BytesReadLastScan, 0, 1024);
    }

    [Fact]
    public void CacheOverflowRotatesFilesAndKeepsTheNewestObservedEventWhenItsCheckpointIsEvicted()
    {
        for (var index = 0; index < 5; index++)
            WriteSession($"rollout-{index}.jsonl", QuotaLine(Now.AddMinutes(index - 5), 20 + index));
        var reader = CreateReader(maxCachedFiles: 2);
        CodexUsageSnapshot? result = null;
        for (var scan = 0; scan < 8; scan++)
        {
            result = reader.ReadLatest();
            Assert.InRange(reader.CachedFileCount, 1, 2);
            Assert.False(reader.LastScanComplete);
        }

        Assert.Equal(24, result!.Primary!.UsedPercent);
        Assert.Contains("scan incomplete", result.Error, StringComparison.Ordinal);
        Assert.Null(result.AccountKey);
    }

    [Fact]
    public void OverflowDoesNotEvictEveryPartialRecordBeforeAnyRecordCanFinish()
    {
        for (var index = 0; index < 3; index++)
            WriteSession($"rollout-{index}.jsonl", QuotaLine(Now.AddMinutes(index - 3), 20 + index));
        var reader = CreateReader(maxBytesPerScan: 128, maxCachedFiles: 2, fileReadQuantum: 128);
        CodexUsageSnapshot? result = null;
        for (var scan = 0; scan < 30; scan++)
        {
            try { result = reader.ReadLatest(); }
            catch (InvalidOperationException) { /* The first complete measurement may require multiple bounded reads. */ }
            Assert.InRange(reader.CachedFileCount, 1, 2);
            Assert.InRange(reader.BytesReadLastScan, 0, 128);
        }

        Assert.NotNull(result);
        Assert.Equal(22, result.Primary!.UsedPercent);
    }

    [Fact]
    public void AnOversizedUnfinishedLineIsDiscardedAndTheNextCompleteRecordCanStillBeRead()
    {
        var path = WriteSession("rollout-one.jsonl", QuotaLine(Now.AddHours(-1), 25));
        var reader = CreateReader(maxLineBytes: 1024);
        reader.ReadLatest();
        File.AppendAllText(path, new string('x', 50_000));
        var unfinished = reader.ReadLatest();
        Assert.Equal(25, unfinished.Primary!.UsedPercent);
        Assert.False(reader.LastScanComplete);
        File.AppendAllText(path, "\n" + QuotaLine(Now, 45));

        var result = reader.ReadLatest();

        Assert.Equal(45, result.Primary!.UsedPercent);
        Assert.True(reader.LastScanComplete);
    }

    [Fact]
    public void PreCancelledReadDoesNotTouchContentOrLoseAnExistingCheckpoint()
    {
        var path = WriteSession("rollout-one.jsonl", QuotaLine(Now.AddHours(-1), 25));
        var reader = CreateReader();
        reader.ReadLatest();
        var appended = QuotaLine(Now, 45);
        File.AppendAllText(path, appended);

        Assert.Throws<OperationCanceledException>(() => reader.ReadLatest(new CancellationToken(canceled: true)));
        Assert.Equal(0, reader.BytesReadLastScan);
        Assert.False(reader.LastScanComplete);

        var result = reader.ReadLatest();

        Assert.Equal(45, result.Primary!.UsedPercent);
        Assert.Equal(Encoding.UTF8.GetByteCount(appended), reader.BytesReadLastScan);
    }

    [Fact]
    public void CancellationDuringDiscoveryStopsBeforeContentReadsAndCanBeRetried()
    {
        WriteSession("rollout-one.jsonl", QuotaLine(Now, 25));
        using var cancellation = new CancellationTokenSource();
        var cancelAtClock = true;
        var reader = new CachedCodexSessionReader(HomePath, clock: () =>
        {
            if (cancelAtClock) cancellation.Cancel();
            return Now;
        });

        Assert.Throws<OperationCanceledException>(() => reader.ReadLatest(cancellation.Token));
        Assert.Equal(0, reader.BytesReadLastScan);
        Assert.Equal(0, reader.CachedFileCount);
        cancelAtClock = false;

        Assert.Equal(25, reader.ReadLatest().Primary!.UsedPercent);
        Assert.True(reader.LastScanComplete);
    }

    [Fact]
    public void MissingSourcesRemainUnavailableWithoutCreatingAnyDirectories()
    {
        var reader = CreateReader();

        Assert.Throws<InvalidOperationException>(() => reader.ReadLatest());

        Assert.False(Directory.Exists(HomePath));
        Assert.Equal(0, reader.BytesReadLastScan);
        Assert.Equal(0, reader.CachedFileCount);
    }

    private CachedCodexSessionReader CreateReader(long maxBytesPerScan = 8 * 1024 * 1024,
        int maxCachedFiles = 512, int maxLineBytes = 128 * 1024, int fileReadQuantum = 64 * 1024) =>
        new(HomePath, maxBytesPerScan, maxCachedFiles, maxLineBytes, fileReadQuantum, () => Now);

    private string WriteSession(string name, string content, bool archived = false)
    {
        var directory = Path.Combine(HomePath, archived ? "archived_sessions" : "sessions", "2026", "09", "09");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
        return path;
    }

    private static string QuotaLine(DateTimeOffset recordedAt, int? primary, int? secondary = 40) =>
        JsonSerializer.Serialize(new
        {
            timestamp = recordedAt,
            payload = new
            {
                rate_limits = new
                {
                    primary = primary is { } shortUsed ? new { used_percent = shortUsed, window_minutes = 300, resets_at = Now.AddHours(4).ToUnixTimeSeconds() } : null,
                    secondary = secondary is { } weeklyUsed ? new { used_percent = weeklyUsed, window_minutes = 10080, resets_at = Now.AddDays(3).ToUnixTimeSeconds() } : null,
                    plan_type = "pro",
                },
            },
        }) + "\n";
}
