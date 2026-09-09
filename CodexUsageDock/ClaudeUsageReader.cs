using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexUsageDock;

internal enum ClaudeUsageReadStatus
{
    Available,
    Partial,
    Unavailable,
    Stale,
    Future,
}

internal sealed record ClaudeUsageWindow(
    double UsedPercent,
    int WindowMinutes,
    DateTimeOffset ResetsAt)
{
    internal double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed record ClaudeUsageSnapshot(
    ClaudeUsageWindow? Primary,
    ClaudeUsageWindow? Weekly,
    DateTimeOffset ObservedAt,
    ClaudeUsageReadStatus Status,
    string Message)
{
    internal bool IsAvailable => Status is ClaudeUsageReadStatus.Available or ClaudeUsageReadStatus.Partial;

    internal static ClaudeUsageSnapshot Unavailable(string message) =>
        new(null, null, DateTimeOffset.MinValue, ClaudeUsageReadStatus.Unavailable, message);
}

internal static class ClaudeUsageReader
{
    internal const int MaximumFileBytes = 64 * 1024;
    private const int PrimaryWindowMinutes = 5 * 60;
    private const int WeeklyWindowMinutes = 7 * 24 * 60;

    internal static ClaudeUsageSnapshot Read(
        string path,
        DateTimeOffset now,
        TimeSpan refreshInterval,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !IsFullyQualifiedPath(path))
        {
            return ClaudeUsageSnapshot.Unavailable("Claude usage capture path must be a fully qualified file path.");
        }

        byte[] bytes;
        try
        {
            bytes = ReadBounded(path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or PathTooLongException or InvalidDataException)
        {
            return ClaudeUsageSnapshot.Unavailable("Claude usage capture could not be read.");
        }

        JsonDocument document;
        try
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var json = encoding.GetString(bytes);
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException)
        {
            return ClaudeUsageSnapshot.Unavailable("Claude usage capture is not valid JSON.");
        }

        using (document)
        {
            return Parse(document.RootElement, now, refreshInterval);
        }
    }

    internal static ClaudeUsageSnapshot Read(string path, DateTimeOffset now) =>
        Read(path, now, UsageFreshness.MinimumAge);

    internal static ClaudeUsageSnapshot Read(string path) =>
        Read(path, DateTimeOffset.UtcNow, UsageFreshness.MinimumAge);

    private static ClaudeUsageSnapshot Parse(
        JsonElement root,
        DateTimeOffset now,
        TimeSpan refreshInterval)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetSchemaVersion(root, out var schemaVersion)
            || schemaVersion != 1
            || !TryGetString(root, "provider", out var provider)
            || !string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase)
            || !TryGetObservedAt(root, out var observedAt))
        {
            return ClaudeUsageSnapshot.Unavailable("Claude usage capture has an unsupported schema or provider.");
        }

        if (!TryGetObject(root, "rate_limits", out var rateLimits))
        {
            return ClaudeUsageSnapshot.Unavailable("Claude usage capture has no rate-limit data.");
        }

        var primary = ParseWindow(rateLimits, "five_hour", PrimaryWindowMinutes, now);
        var weekly = ParseWindow(rateLimits, "seven_day", WeeklyWindowMinutes, now);
        var freshness = UsageFreshness.Classify(observedAt, now, refreshInterval);
        if (freshness == UsageFreshnessState.Future)
        {
            return new ClaudeUsageSnapshot(primary.Window, weekly.Window, observedAt, ClaudeUsageReadStatus.Future,
                "Claude usage capture timestamp is in the future.");
        }

        if (freshness == UsageFreshnessState.Stale)
        {
            return new ClaudeUsageSnapshot(primary.Window, weekly.Window, observedAt, ClaudeUsageReadStatus.Stale,
                "Claude usage capture is stale.");
        }

        if (freshness != UsageFreshnessState.Fresh)
        {
            return new ClaudeUsageSnapshot(primary.Window, weekly.Window, observedAt, ClaudeUsageReadStatus.Unavailable,
                "Claude usage capture freshness is unavailable.");
        }

        var validCount = (primary.Window is null ? 0 : 1) + (weekly.Window is null ? 0 : 1);
        var status = validCount switch
        {
            2 => ClaudeUsageReadStatus.Available,
            1 => ClaudeUsageReadStatus.Partial,
            _ => ClaudeUsageReadStatus.Unavailable,
        };
        var message = validCount switch
        {
            2 => "Claude rate-limit windows are available.",
            1 => "One Claude rate-limit window is unavailable; windows remain independent.",
            _ => "No valid Claude rate-limit windows were provided.",
        };
        return new ClaudeUsageSnapshot(primary.Window, weekly.Window, observedAt, status, message);
    }

    private static WindowParseResult ParseWindow(
        JsonElement parent,
        string propertyName,
        int windowMinutes,
        DateTimeOffset now)
    {
        if (!parent.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new WindowParseResult(null);
        }

        if (value.ValueKind != JsonValueKind.Object
            || !TryGetNumber(value, "used_percentage", out var usedPercent)
            || !TryGetReset(value, "resets_at", out var resetsAt))
        {
            return new WindowParseResult(null);
        }

        var rateWindow = new RateLimitWindow(usedPercent, windowMinutes, resetsAt);
        return UsageFreshness.IsValidWindow(rateWindow, now)
            ? new WindowParseResult(new ClaudeUsageWindow(usedPercent, windowMinutes, resetsAt.ToUniversalTime()))
            : new WindowParseResult(null);
    }

    private static bool TryGetSchemaVersion(JsonElement root, out int version)
    {
        version = 0;
        return root.TryGetProperty("schemaVersion", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out version);
    }

    private static bool TryGetString(JsonElement parent, string propertyName, out string value)
    {
        value = string.Empty;
        return parent.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
            && (value = element.GetString() ?? string.Empty).Length > 0;
    }

    private static bool TryGetObservedAt(JsonElement root, out DateTimeOffset observedAt)
    {
        observedAt = default;
        if (!TryGetString(root, "observedAtUTC", out var value)
            || !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out observedAt)
            || !HasExplicitOffset(value))
        {
            observedAt = default;
            return false;
        }

        observedAt = observedAt.ToUniversalTime();
        return true;
    }

    private static bool TryGetObject(JsonElement parent, string propertyName, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetNumber(
        JsonElement parent,
        string propertyName,
        out double number)
    {
        number = 0;
        if (parent.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out number)
            && double.IsFinite(number)
            && number is >= 0 and <= 100)
        {
            return true;
        }

        number = 0;
        return false;
    }

    private static bool TryGetReset(
        JsonElement parent,
        string propertyName,
        out DateTimeOffset resetsAt)
    {
        resetsAt = default;
        if (parent.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var seconds))
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return false;
    }

    private static byte[] ReadBounded(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 8192,
            options: FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("Claude usage capture is oversized.");
        }

        using var memory = new MemoryStream((int)stream.Length);
        var buffer = new byte[8192];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > MaximumFileBytes)
            {
                throw new InvalidDataException("Claude usage capture is oversized.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private static bool IsFullyQualifiedPath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasExplicitOffset(string value)
    {
        var text = value.Trim();
        if (text.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var timeSeparator = text.IndexOf('T');
        if (timeSeparator < 0)
        {
            timeSeparator = text.IndexOf(' ');
        }

        if (timeSeparator < 0 || timeSeparator == text.Length - 1)
        {
            return false;
        }

        var time = text[(timeSeparator + 1)..];
        return time.Contains('+', StringComparison.Ordinal)
            || time.LastIndexOf('-') > 0;
    }

    private readonly record struct WindowParseResult(ClaudeUsageWindow? Window);
}
