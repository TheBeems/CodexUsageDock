using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageDock;

internal sealed class LocalCodexTokenUsageReader
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumTokenEventLineBytes = 256 * 1024;

    private readonly string _codexHome;
    private readonly Dictionary<string, CachedTokenFile> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal LocalCodexTokenUsageReader(string? codexHome = null)
    {
        _codexHome = codexHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
    }

    internal Task<LocalTokenUsageSnapshot> ReadAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(windowStart, windowEnd, timeZone, cancellationToken), cancellationToken);

    private LocalTokenUsageSnapshot Read(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (windowEnd < windowStart)
        {
            throw new ArgumentOutOfRangeException(nameof(windowEnd), "The token usage window cannot end before it starts.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceAvailable = false;
        var partial = false;
        foreach (var (directory, recursive) in GetSourceDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                continue;
            }

            sourceAvailable = true;
            try
            {
                foreach (var path in Directory.EnumerateFiles(
                    directory,
                    "rollout-*.jsonl",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                {
                    paths.Add(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                partial = true;
            }
        }

        if (!sourceAvailable)
        {
            return LocalTokenUsageSnapshot.Unavailable with { UpdatedAt = DateTimeOffset.Now };
        }

        foreach (var path in paths.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.GetLastWriteTimeUtc(path) < windowStart.UtcDateTime)
                {
                    paths.Remove(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                paths.Remove(path);
                partial = true;
            }
        }

        foreach (var stalePath in _cache.Keys.Where(path => !paths.Contains(path)).ToArray())
        {
            _cache.Remove(stalePath);
        }

        var successfullyReadFiles = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var cache = RefreshFile(path, cancellationToken);
                _cache[path] = cache;
                successfullyReadFiles++;
                partial |= cache.HadMalformedTokenEvent;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                partial = true;
            }
        }

        if (paths.Count > 0 && successfullyReadFiles == 0)
        {
            return LocalTokenUsageSnapshot.Unavailable with { UpdatedAt = DateTimeOffset.Now };
        }

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var totals = new Dictionary<DateOnly, long>();
        foreach (var tokenEvent in _cache.Values
                     .SelectMany(file => file.Events)
                     .Where(tokenEvent => tokenEvent.RecordedAt >= windowStart && tokenEvent.RecordedAt <= windowEnd)
                     .OrderBy(tokenEvent => tokenEvent.RecordedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fingerprints.Add(tokenEvent.Fingerprint))
            {
                continue;
            }

            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(tokenEvent.RecordedAt, timeZone).Date);
            totals[localDate] = checked(totals.GetValueOrDefault(localDate) + tokenEvent.TotalTokens);
        }

        return new LocalTokenUsageSnapshot(
            totals.OrderBy(pair => pair.Key).Select(pair => new DailyTokenUsage(pair.Key, pair.Value)).ToArray(),
            DateTimeOffset.Now,
            partial ? LocalTokenUsageStatus.Partial : LocalTokenUsageStatus.Complete);
    }

    private IEnumerable<(string Directory, bool Recursive)> GetSourceDirectories()
    {
        yield return (Path.Combine(_codexHome, "sessions"), true);
        yield return (Path.Combine(_codexHome, "archived_sessions"), false);
    }

    private CachedTokenFile RefreshFile(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        var observedLength = file.Length;
        var lastWriteTimeUtc = file.LastWriteTimeUtc;
        if (_cache.TryGetValue(path, out var cached)
            && cached.ObservedLength == observedLength
            && cached.LastWriteTimeUtc == lastWriteTimeUtc)
        {
            return cached;
        }

        var canAppend = cached is not null
            && observedLength >= cached.ObservedLength
            && lastWriteTimeUtc >= cached.LastWriteTimeUtc;
        var events = canAppend ? new List<TokenUsageEvent>(cached!.Events) : [];
        var readOffset = canAppend ? cached!.ReadOffset : 0;
        var lastCumulativeTotal = canAppend ? cached!.LastCumulativeTotal : null;
        var malformed = canAppend && cached!.HadMalformedTokenEvent;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan);
        if (readOffset > stream.Length)
        {
            events.Clear();
            readOffset = 0;
            lastCumulativeTotal = null;
            malformed = false;
        }

        stream.Position = readOffset;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var line = new MemoryStream();
        var lineTooLong = false;
        var completedOffset = readOffset;
        var absoluteOffset = readOffset;
        try
        {
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var index = 0; index < bytesRead; index++)
                {
                    var value = buffer[index];
                    absoluteOffset++;
                    if (value == (byte)'\n')
                    {
                        if (!lineTooLong && line.Length > 0)
                        {
                            var result = TryParseTokenEvent(line.GetBuffer().AsSpan(0, checked((int)line.Length)), lastCumulativeTotal);
                            if (result.IsMalformed)
                            {
                                malformed = true;
                            }

                            if (result.Event is { } tokenEvent)
                            {
                                events.Add(tokenEvent);
                                lastCumulativeTotal = tokenEvent.CumulativeTotalTokens;
                            }
                        }

                        line.SetLength(0);
                        lineTooLong = false;
                        completedOffset = absoluteOffset;
                        continue;
                    }

                    if (value == (byte)'\r' || lineTooLong)
                    {
                        continue;
                    }

                    if (line.Length >= MaximumTokenEventLineBytes)
                    {
                        line.SetLength(0);
                        lineTooLong = true;
                        continue;
                    }

                    line.WriteByte(value);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new CachedTokenFile(
            observedLength,
            lastWriteTimeUtc,
            completedOffset,
            lastCumulativeTotal,
            events,
            malformed);
    }

    private static TokenEventParseResult TryParseTokenEvent(ReadOnlySpan<byte> line, long? lastCumulativeTotal)
    {
        if (line.IndexOf("\"token_count\""u8) < 0)
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var entryType)
                || entryType.ValueKind != JsonValueKind.String
                || entryType.GetString() != "event_msg"
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("type", out var payloadType)
                || payloadType.ValueKind != JsonValueKind.String
                || payloadType.GetString() != "token_count")
            {
                return default;
            }

            if (!TryReadUsage(payload, out var usage))
            {
                return default;
            }

            if (lastCumulativeTotal.HasValue && usage.CumulativeTotalTokens <= lastCumulativeTotal.Value)
            {
                return default;
            }

            if (!root.TryGetProperty("timestamp", out var timestampValue)
                || timestampValue.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    timestampValue.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var recordedAt))
            {
                return new TokenEventParseResult(null, IsMalformed: true);
            }

            var fingerprintInput = FormattableString.Invariant(
                $"{recordedAt.UtcTicks}|{usage.InputTokens}|{usage.CachedInputTokens}|{usage.OutputTokens}|{usage.ReasoningOutputTokens}|{usage.TotalTokens}|{usage.CumulativeTotalTokens}");
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)));
            return new TokenEventParseResult(
                new TokenUsageEvent(recordedAt, usage.TotalTokens, usage.CumulativeTotalTokens, fingerprint),
                IsMalformed: false);
        }
        catch (JsonException)
        {
            return new TokenEventParseResult(null, IsMalformed: true);
        }
    }

    private static bool TryReadUsage(JsonElement payload, out ParsedUsage usage)
    {
        usage = default;
        if (!payload.TryGetProperty("info", out var info)
            || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("last_token_usage", out var lastUsage)
            || lastUsage.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("total_token_usage", out var totalUsage)
            || totalUsage.ValueKind != JsonValueKind.Object
            || !TryGetNonNegativeInt64(lastUsage, "total_tokens", out var totalTokens)
            || !TryGetNonNegativeInt64(totalUsage, "total_tokens", out var cumulativeTotalTokens))
        {
            return false;
        }

        usage = new ParsedUsage(
            GetOptionalNonNegativeInt64(lastUsage, "input_tokens"),
            GetOptionalNonNegativeInt64(lastUsage, "cached_input_tokens"),
            GetOptionalNonNegativeInt64(lastUsage, "output_tokens"),
            GetOptionalNonNegativeInt64(lastUsage, "reasoning_output_tokens"),
            totalTokens,
            cumulativeTotalTokens);
        return true;
    }

    private static bool TryGetNonNegativeInt64(JsonElement owner, string propertyName, out long value)
    {
        value = 0;
        return owner.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value >= 0;
    }

    private static long GetOptionalNonNegativeInt64(JsonElement owner, string propertyName) =>
        TryGetNonNegativeInt64(owner, propertyName, out var value) ? value : -1;

    private sealed record CachedTokenFile(
        long ObservedLength,
        DateTime LastWriteTimeUtc,
        long ReadOffset,
        long? LastCumulativeTotal,
        IReadOnlyList<TokenUsageEvent> Events,
        bool HadMalformedTokenEvent);

    private sealed record TokenUsageEvent(
        DateTimeOffset RecordedAt,
        long TotalTokens,
        long CumulativeTotalTokens,
        string Fingerprint);

    private readonly record struct ParsedUsage(
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        long ReasoningOutputTokens,
        long TotalTokens,
        long CumulativeTotalTokens);

    private readonly record struct TokenEventParseResult(TokenUsageEvent? Event, bool IsMalformed);
}
