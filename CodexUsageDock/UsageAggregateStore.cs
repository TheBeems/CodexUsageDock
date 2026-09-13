using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageDock;

internal sealed record UsageAggregatePoint(
    DateTimeOffset RecordedAt,
    double? PrimaryRemainingPercent,
    double? WeeklyRemainingPercent,
    DateTimeOffset? PrimaryResetsAt,
    DateTimeOffset? WeeklyResetsAt,
    UsageDataSource Source);

internal sealed record UsageAggregateStoreDocument(
    int SchemaVersion,
    int RetentionDays,
    UsageAggregatePoint?[]? Observations);

internal sealed class UsageAggregateStore
{
    internal const int SchemaVersion = 1;
    internal const int MaximumEntries = 27_000;
    internal const int DefaultRetentionDays = 30;
    internal const int MaximumDocumentBytes = 16 * 1024 * 1024;

    private static readonly TimeSpan ObservationBucket = TimeSpan.FromMinutes(5);
    private const string FileName = "usage-aggregates.json";
    private const string LoadErrorMessage = "Saved usage observations could not be read. A new history will be collected.";
    private const string SaveErrorMessage = "Usage observations could not be saved. They may be lost after restarting.";
    private const string InvalidObservationMessage = "The usage observation was invalid and was not saved.";
    private const string InvalidRetentionMessage = "Retention must be 7, 30, or 90 days.";
    private const string ClearErrorMessage = "Usage observations could not be cleared.";
    private const string ExportCaveat = "Local quota observations only; no tokens, costs, account identifiers, or session content.";
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private List<UsageAggregatePoint> _observations;
    private int _retentionDays;
    private string? _storageError;

    internal UsageAggregateStore(
        string path,
        int retentionDays = DefaultRetentionDays,
        Func<DateTimeOffset>? clock = null)
    {
        _path = Path.GetFullPath(path);
        _retentionDays = NormalizeRetentionDays(retentionDays);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _observations = Load();
    }

    internal static UsageAggregateStore CreateDefault() => new(LocalStorage.GetPath(FileName));

    internal UsageAggregateStore ForContext(string context) =>
        new(LocalStorage.ContextPath(_path, context), _retentionDays, _clock);

    internal int RetentionDays
    {
        get
        {
            lock (_gate)
            {
                return _retentionDays;
            }
        }
    }

    internal string? StorageError
    {
        get
        {
            lock (_gate)
            {
                return _storageError;
            }
        }
    }

    internal IReadOnlyList<UsageAggregatePoint> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _observations.ToArray();
            }
        }
    }

    internal IReadOnlyList<UsageAggregatePoint> Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!TryNormalizeNow(now, out var normalizedNow))
            {
                return _observations.ToArray();
            }

            var normalized = Normalize(_observations, normalizedNow, _retentionDays, out var changed);
            if (changed)
            {
                var previous = _observations;
                if (TrySave(normalized, out _))
                {
                    _observations = normalized;
                }
                else
                {
                    _observations = previous;
                }
            }

            return _observations.ToArray();
        }
    }

    internal bool SetRetentionDays(int retentionDays, DateTimeOffset now) =>
        SetRetentionDays(retentionDays, now, out _);

    internal bool SetRetentionDays(int retentionDays, DateTimeOffset now, out string? error)
    {
        error = null;
        if (!TryGetSupportedRetention(retentionDays, out var normalizedRetention))
        {
            error = InvalidRetentionMessage;
            SetStorageError(error);
            return false;
        }

        if (!TryNormalizeNow(now, out var normalizedNow))
        {
            error = InvalidObservationMessage;
            SetStorageError(error);
            return false;
        }

        lock (_gate)
        {
            var previousRetention = _retentionDays;
            var previousObservations = _observations;
            _retentionDays = normalizedRetention;
            var normalized = Normalize(_observations, normalizedNow, _retentionDays, out _);
            if (!TrySave(normalized, out error))
            {
                _retentionDays = previousRetention;
                _observations = previousObservations;
                return false;
            }

            _observations = normalized;
            return true;
        }
    }

    internal bool Record(UsageAggregatePoint point) =>
        Record(point, _clock(), out _);

    internal bool Record(UsageAggregatePoint point, out string? error) =>
        Record(point, _clock(), out error);

    internal bool Record(UsageAggregatePoint point, DateTimeOffset now) =>
        Record(point, now, out _);

    internal bool Record(UsageAggregatePoint point, DateTimeOffset now, out string? error)
    {
        error = null;
        if (!TryNormalizeNow(now, out var normalizedNow)
            || !TryNormalizePoint(point, normalizedNow, out var normalizedPoint))
        {
            error = InvalidObservationMessage;
            SetStorageError(error);
            return false;
        }

        lock (_gate)
        {
            var previous = _observations;
            var updated = new List<UsageAggregatePoint>(previous);
            var key = GetObservationKey(normalizedPoint);
            var existingIndex = FindLastIndex(updated, key);
            if (existingIndex >= 0)
            {
                if (updated[existingIndex].RecordedAt > normalizedPoint.RecordedAt)
                {
                    return true;
                }

                updated[existingIndex] = normalizedPoint;
            }
            else
            {
                updated.Add(normalizedPoint);
            }

            updated = Normalize(updated, normalizedNow, _retentionDays, out _);
            if (!TrySave(updated, out error))
            {
                _observations = previous;
                return false;
            }

            _observations = updated;
            return true;
        }
    }

    internal bool Clear() => Clear(out _);

    internal bool Clear(out string? error)
    {
        error = null;
        lock (_gate)
        {
            var previous = _observations;
            if (!TrySave([], out error))
            {
                _observations = previous;
                error ??= ClearErrorMessage;
                return false;
            }

            _observations = [];
            return true;
        }
    }

    internal string ExportJson() => ExportJson(_clock());

    internal string ExportJson(DateTimeOffset now)
    {
        var observations = Read(now);
        var output = new StringBuilder(Math.Min(1_000_000, observations.Count * 180 + 512));
        output.Append("{\n");
        output.Append("  \"schemaVersion\": 1,\n");
        output.Append("  \"units\": {\n");
        output.Append("    \"timestamps\": \"UTC ISO 8601\",\n");
        output.Append("    \"remainingPercent\": \"percent (0-100)\"\n");
        output.Append("  },\n");
        output.Append("  \"caveat\": ").Append(Quote(ExportCaveat)).Append(",\n");
        output.Append("  \"observations\": [");
        for (var index = 0; index < observations.Count; index++)
        {
            if (index == 0)
            {
                output.Append('\n');
            }
            else
            {
                output.Append(",\n");
            }

            AppendJsonObservation(output, observations[index]);
        }

        if (observations.Count > 0)
        {
            output.Append('\n');
        }

        output.Append("  ]\n");
        output.Append('}');
        return output.ToString();
    }

    internal string ExportCsv() => ExportCsv(_clock());

    internal string ExportCsv(DateTimeOffset now)
    {
        var observations = Read(now);
        var output = new StringBuilder(Math.Min(1_000_000, observations.Count * 160 + 512));
        output.Append("# schemaVersion=1\n");
        output.Append("# units=recordedAtUtc and reset timestamps are UTC ISO 8601; remainingPercent fields are percent from 0 to 100\n");
        output.Append("# caveat=Local quota observations only; no tokens, costs, account identifiers, or session content\n");
        output.Append("recordedAtUtc,primaryRemainingPercent,weeklyRemainingPercent,primaryResetsAtUtc,weeklyResetsAtUtc,source\n");
        foreach (var observation in observations)
        {
            output.Append(FormatUtc(observation.RecordedAt)).Append(',');
            AppendCsvNumber(output, observation.PrimaryRemainingPercent).Append(',');
            AppendCsvNumber(output, observation.WeeklyRemainingPercent).Append(',');
            AppendCsvTimestamp(output, observation.PrimaryResetsAt).Append(',');
            AppendCsvTimestamp(output, observation.WeeklyResetsAt).Append(',');
            output.Append(observation.Source.ToString()).Append('\n');
        }

        return output.ToString();
    }

    internal static bool TryCreateLivePoint(
        CodexUsageSnapshot? snapshot,
        DateTimeOffset now,
        TimeSpan refreshInterval,
        out UsageAggregatePoint? point)
    {
        point = null;
        if (snapshot is null
            || snapshot.Source != UsageDataSource.AppServer
            || string.IsNullOrWhiteSpace(snapshot.AccountKey)
            || !TryNormalizeNow(now, out var normalizedNow)
            || snapshot.UpdatedAt > normalizedNow
            || !UsageFreshness.IsFresh(snapshot.UpdatedAt, normalizedNow, refreshInterval))
        {
            return false;
        }

        var primary = TryGetWindowValues(snapshot.Primary, normalizedNow);
        var weekly = TryGetWindowValues(snapshot.Secondary, normalizedNow);
        if (primary is null && weekly is null)
        {
            return false;
        }

        point = new UsageAggregatePoint(
            snapshot.UpdatedAt.ToUniversalTime(),
            primary?.RemainingPercent,
            weekly?.RemainingPercent,
            primary?.ResetsAt,
            weekly?.ResetsAt,
            UsageDataSource.AppServer);
        return true;
    }

    private List<UsageAggregatePoint> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var fileInfo = new FileInfo(_path);
            if (fileInfo.Length > MaximumDocumentBytes)
            {
                SetStorageError(LoadErrorMessage);
                return [];
            }

            var document = JsonSerializer.Deserialize(
                File.ReadAllText(_path),
                UsageAggregateStoreJsonContext.Default.UsageAggregateStoreDocument);
            if (document is null
                || document.SchemaVersion != SchemaVersion
                || document.Observations is null
                || !TryNormalizeNow(_clock(), out var now))
            {
                SetStorageError(LoadErrorMessage);
                return [];
            }

            var normalized = Normalize(document.Observations, now, _retentionDays, out var changed);
            if (changed)
            {
                _ = TrySave(normalized, out _);
            }

            return normalized;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException)
        {
            LocalStorage.TraceFailure("load usage observations", error);
            SetStorageError(LoadErrorMessage);
            return [];
        }
    }

    private bool TrySave(IReadOnlyList<UsageAggregatePoint> observations, out string? error)
    {
        error = null;
        try
        {
            var document = new UsageAggregateStoreDocument(
                SchemaVersion,
                _retentionDays,
                observations.ToArray());
            var json = JsonSerializer.Serialize(
                document,
                UsageAggregateStoreJsonContext.Default.UsageAggregateStoreDocument);
            if (!LocalStorage.TryWrite(_path, json))
            {
                error = SaveErrorMessage;
                SetStorageError(error);
                return false;
            }

            SetStorageError(null);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            LocalStorage.TraceFailure("save usage observations", exception);
            error = SaveErrorMessage;
            SetStorageError(error);
            return false;
        }
    }

    private static List<UsageAggregatePoint> Normalize(
        IEnumerable<UsageAggregatePoint?> observations,
        DateTimeOffset now,
        int retentionDays,
        out bool changed)
    {
        var original = observations.ToArray();
        var accepted = new List<UsageAggregatePoint>();
        foreach (var observation in original)
        {
            if (observation is null || !TryNormalizePoint(observation, now, out var normalized))
            {
                continue;
            }

            if (!IsWithinRetention(normalized.RecordedAt, now, retentionDays))
            {
                continue;
            }

            accepted.Add(normalized);
        }

        accepted.Sort(static (left, right) => left.RecordedAt.CompareTo(right.RecordedAt));
        var deduplicated = new List<UsageAggregatePoint>(Math.Min(accepted.Count, MaximumEntries));
        var indexes = new Dictionary<ObservationKey, int>();
        foreach (var observation in accepted)
        {
            var key = GetObservationKey(observation);
            if (indexes.TryGetValue(key, out var index))
            {
                deduplicated[index] = observation;
            }
            else
            {
                indexes[key] = deduplicated.Count;
                deduplicated.Add(observation);
            }
        }

        if (deduplicated.Count > MaximumEntries)
        {
            deduplicated = deduplicated.TakeLast(MaximumEntries).ToList();
        }

        changed = accepted.Count != original.Length
            || deduplicated.Count != accepted.Count
            || !deduplicated.SequenceEqual(original.Where(observation => observation is not null)!.Cast<UsageAggregatePoint>());
        return deduplicated;
    }

    private static bool IsWithinRetention(DateTimeOffset recordedAt, DateTimeOffset now, int retentionDays)
    {
        try
        {
            var age = now - recordedAt;
            return age >= TimeSpan.Zero && age <= TimeSpan.FromDays(retentionDays);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryNormalizePoint(
        UsageAggregatePoint point,
        DateTimeOffset now,
        out UsageAggregatePoint normalized)
    {
        normalized = point;
        if (point.Source != UsageDataSource.AppServer
            || !IsValidTimestamp(point.RecordedAt)
            || point.RecordedAt > now
            || !IsValidPercent(point.PrimaryRemainingPercent)
            || !IsValidPercent(point.WeeklyRemainingPercent))
        {
            return false;
        }

        if (point.PrimaryRemainingPercent is null && point.WeeklyRemainingPercent is null)
        {
            return false;
        }

        if (!TryNormalizeReset(point.PrimaryResetsAt, point.RecordedAt, out var primaryReset)
            || !TryNormalizeReset(point.WeeklyResetsAt, point.RecordedAt, out var weeklyReset))
        {
            return false;
        }

        normalized = new UsageAggregatePoint(
            point.RecordedAt.ToUniversalTime(),
            point.PrimaryRemainingPercent,
            point.WeeklyRemainingPercent,
            point.PrimaryRemainingPercent is null ? null : primaryReset,
            point.WeeklyRemainingPercent is null ? null : weeklyReset,
            UsageDataSource.AppServer);
        return true;
    }

    private static bool TryNormalizeReset(
        DateTimeOffset? reset,
        DateTimeOffset recordedAt,
        out DateTimeOffset? normalized)
    {
        normalized = null;
        if (reset is not { } value)
        {
            return true;
        }

        if (!IsValidTimestamp(value) || value < recordedAt)
        {
            return false;
        }

        normalized = value.ToUniversalTime();
        return true;
    }

    private static (double RemainingPercent, DateTimeOffset ResetsAt)? TryGetWindowValues(
        RateLimitWindow? window,
        DateTimeOffset now)
    {
        if (!UsageFreshness.IsValidWindow(window, now)
            || !IsValidTimestamp(window!.ResetsAt))
        {
            return null;
        }

        return (window!.RemainingPercent, window.ResetsAt.ToUniversalTime());
    }

    private static bool IsValidPercent(double? value) =>
        value is null || double.IsFinite(value.Value) && value.Value is >= 0 and <= 100;

    private static bool IsValidTimestamp(DateTimeOffset value) =>
        value > DateTimeOffset.MinValue && value < DateTimeOffset.MaxValue;

    private static bool TryNormalizeNow(DateTimeOffset now, out DateTimeOffset normalized)
    {
        normalized = now.ToUniversalTime();
        return IsValidTimestamp(normalized);
    }

    private static int NormalizeRetentionDays(int retentionDays) =>
        TryGetSupportedRetention(retentionDays, out var normalized) ? normalized : DefaultRetentionDays;

    private static bool TryGetSupportedRetention(int retentionDays, out int normalized)
    {
        normalized = retentionDays switch
        {
            7 or 30 or 90 => retentionDays,
            _ => 0,
        };
        return normalized != 0;
    }

    private static int FindLastIndex(List<UsageAggregatePoint> observations, ObservationKey key)
    {
        for (var index = observations.Count - 1; index >= 0; index--)
        {
            if (GetObservationKey(observations[index]) == key)
            {
                return index;
            }
        }

        return -1;
    }

    private static ObservationKey GetObservationKey(UsageAggregatePoint observation) => new(
        observation.RecordedAt.UtcTicks / ObservationBucket.Ticks,
        observation.PrimaryResetsAt?.UtcTicks,
        observation.WeeklyResetsAt?.UtcTicks);

    private static void AppendJsonObservation(StringBuilder output, UsageAggregatePoint observation)
    {
        output.Append("    {\"recordedAtUtc\": ")
            .Append(Quote(FormatUtc(observation.RecordedAt)))
            .Append(", \"primaryRemainingPercent\": ");
        AppendJsonNumber(output, observation.PrimaryRemainingPercent);
        output.Append(", \"weeklyRemainingPercent\": ");
        AppendJsonNumber(output, observation.WeeklyRemainingPercent);
        output.Append(", \"primaryResetsAtUtc\": ");
        AppendJsonTimestamp(output, observation.PrimaryResetsAt);
        output.Append(", \"weeklyResetsAtUtc\": ");
        AppendJsonTimestamp(output, observation.WeeklyResetsAt);
        output.Append(", \"source\": ").Append(Quote(observation.Source.ToString())).Append('}');
    }

    private static void AppendJsonNumber(StringBuilder output, double? value) =>
        output.Append(value is { } number ? number.ToString("R", CultureInfo.InvariantCulture) : "null");

    private static void AppendJsonTimestamp(StringBuilder output, DateTimeOffset? value) =>
        output.Append(value is { } timestamp ? Quote(FormatUtc(timestamp)) : "null");

    private static StringBuilder AppendCsvNumber(StringBuilder output, double? value) =>
        output.Append(value is { } number ? number.ToString("R", CultureInfo.InvariantCulture) : string.Empty);

    private static StringBuilder AppendCsvTimestamp(StringBuilder output, DateTimeOffset? value) =>
        output.Append(value is { } timestamp ? FormatUtc(timestamp) : string.Empty);

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string Quote(string value) =>
        "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

    private void SetStorageError(string? error)
    {
        lock (_gate)
        {
            _storageError = error;
        }
    }

    private readonly record struct ObservationKey(long Bucket, long? PrimaryResetTicks, long? WeeklyResetTicks);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UsageAggregateStoreDocument))]
[JsonSerializable(typeof(UsageAggregatePoint))]
internal sealed partial class UsageAggregateStoreJsonContext : JsonSerializerContext
{
}
