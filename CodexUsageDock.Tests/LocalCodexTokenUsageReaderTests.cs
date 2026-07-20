using System.Text.Json;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class LocalCodexTokenUsageReaderTests
{
    [Fact]
    public async Task ReaderAggregatesActiveAndArchivedLogsAndDeduplicatesCopiedHistory()
    {
        var codexHome = CreateCodexHome();
        var activePath = Path.Combine(codexHome, "sessions", "2026", "07", "20", "rollout-active.jsonl");
        var archivedPath = Path.Combine(codexHome, "archived_sessions", "rollout-archived.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(activePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(archivedPath)!);
        var first = TokenLine("2026-07-19T22:30:00Z", 100, 40, 20, 5, 120, 120);
        var second = TokenLine("2026-07-20T00:30:00Z", 200, 80, 30, 10, 230, 350);
        File.WriteAllLines(activePath, [first, first, second]);
        File.WriteAllLines(
            archivedPath,
            [
                first,
                second,
                TokenLine("2026-07-20T01:30:00Z", 50, 10, 5, 1, 55, 405),
            ]);
        File.SetLastWriteTimeUtc(activePath, new DateTime(2026, 7, 20, 2, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(archivedPath, new DateTime(2026, 7, 20, 2, 0, 0, DateTimeKind.Utc));

        try
        {
            var reader = new LocalCodexTokenUsageReader(codexHome);
            var timeZone = TimeZoneInfo.CreateCustomTimeZone("UTC+2 test", TimeSpan.FromHours(2), "UTC+2 test", "UTC+2 test");

            var result = await reader.ReadAsync(
                new DateTimeOffset(2026, 7, 19, 20, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 20, 2, 0, 0, TimeSpan.Zero),
                timeZone);

            Assert.Equal(LocalTokenUsageStatus.Complete, result.Status);
            var day = Assert.Single(result.Days);
            Assert.Equal(new DateOnly(2026, 7, 20), day.Date);
            Assert.Equal(405, day.TotalTokens);
        }
        finally
        {
            Directory.Delete(codexHome, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderReadsOnlyAppendedCompleteLinesOnLaterRefreshes()
    {
        var codexHome = CreateCodexHome();
        var path = Path.Combine(codexHome, "sessions", "rollout-active.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, TokenLine("2026-07-20T10:00:00Z", 100, 0, 10, 0, 110, 110) + Environment.NewLine);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc));

        try
        {
            var reader = new LocalCodexTokenUsageReader(codexHome);
            var start = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
            var end = start.AddDays(1);
            var first = await reader.ReadAsync(start, end, TimeZoneInfo.Utc);

            File.AppendAllText(path, TokenLine("2026-07-20T11:00:00Z", 200, 50, 20, 5, 220, 330));
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 7, 20, 11, 0, 0, DateTimeKind.Utc));
            var incomplete = await reader.ReadAsync(start, end, TimeZoneInfo.Utc);
            File.AppendAllText(path, Environment.NewLine);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 7, 20, 11, 1, 0, DateTimeKind.Utc));
            var completed = await reader.ReadAsync(start, end, TimeZoneInfo.Utc);

            Assert.Equal(110, Assert.Single(first.Days).TotalTokens);
            Assert.Equal(110, Assert.Single(incomplete.Days).TotalTokens);
            Assert.Equal(330, Assert.Single(completed.Days).TotalTokens);
        }
        finally
        {
            Directory.Delete(codexHome, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderMarksMalformedTokenEventsAsPartialButKeepsValidTotals()
    {
        var codexHome = CreateCodexHome();
        var path = Path.Combine(codexHome, "sessions", "rollout-active.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(
            path,
            [
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"}",
                TokenLine("2026-07-20T10:00:00Z", 100, 0, 10, 0, 110, 110),
            ]);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc));

        try
        {
            var reader = new LocalCodexTokenUsageReader(codexHome);
            var result = await reader.ReadAsync(
                new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
                TimeZoneInfo.Utc);

            Assert.Equal(LocalTokenUsageStatus.Partial, result.Status);
            Assert.Equal(110, Assert.Single(result.Days).TotalTokens);
        }
        finally
        {
            Directory.Delete(codexHome, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderReturnsUnavailableWhenNoCodexLogSourcesExist()
    {
        var codexHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var reader = new LocalCodexTokenUsageReader(codexHome);

        var result = await reader.ReadAsync(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Utc);

        Assert.Equal(LocalTokenUsageStatus.Unavailable, result.Status);
        Assert.Empty(result.Days);
    }

    [Fact]
    public async Task ServicePublishesTokenUsageWithoutMakingAllowanceDependOnIt()
    {
        var now = DateTimeOffset.Now;
        var snapshot = CodexUsageSnapshot.Loading with
        {
            Secondary = new RateLimitWindow(20, 10080, now.AddDays(3)),
            UpdatedAt = now,
            Source = UsageDataSource.AppServer,
            Error = null,
        };
        var expected = new LocalTokenUsageSnapshot(
            [new DailyTokenUsage(DateOnly.FromDateTime(now.Date), 123_456)],
            now,
            LocalTokenUsageStatus.Complete);
        using var service = new CodexUsageService(
            _ => Task.FromResult(snapshot),
            () => snapshot,
            localTokenUsageReader: (_, _, _, _) => Task.FromResult(expected));

        await service.RefreshAsync();

        Assert.Equal(UsageDataSource.AppServer, service.Current.Source);
        Assert.Same(expected, service.CurrentTokenUsage);
    }

    [Fact]
    public async Task ServiceKeepsAllowanceWhenTokenUsageReadFails()
    {
        var now = DateTimeOffset.Now;
        var snapshot = CodexUsageSnapshot.Loading with
        {
            Secondary = new RateLimitWindow(20, 10080, now.AddDays(3)),
            UpdatedAt = now,
            Source = UsageDataSource.AppServer,
            Error = null,
        };
        using var service = new CodexUsageService(
            _ => Task.FromResult(snapshot),
            () => snapshot,
            localTokenUsageReader: (_, _, _, _) => Task.FromException<LocalTokenUsageSnapshot>(new IOException("test failure")));

        await service.RefreshAsync();

        Assert.Equal(UsageDataSource.AppServer, service.Current.Source);
        Assert.Equal(LocalTokenUsageStatus.Unavailable, service.CurrentTokenUsage.Status);
    }

    private static string CreateCodexHome()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string TokenLine(
        string timestamp,
        long inputTokens,
        long cachedInputTokens,
        long outputTokens,
        long reasoningOutputTokens,
        long totalTokens,
        long cumulativeTotalTokens) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = new
                    {
                        input_tokens = inputTokens,
                        cached_input_tokens = cachedInputTokens,
                        output_tokens = outputTokens,
                        reasoning_output_tokens = reasoningOutputTokens,
                        total_tokens = totalTokens,
                    },
                    total_token_usage = new
                    {
                        total_tokens = cumulativeTotalTokens,
                    },
                },
            },
        });
}
