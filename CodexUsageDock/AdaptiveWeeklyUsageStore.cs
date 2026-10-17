using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageDock;

internal sealed record HourlyUsageBucket(DateTimeOffset StartsAt, long ObservedTicks, double ConsumedPercent);

internal sealed record HourlyUsageHistory(
    HourlyUsageBucket[] Buckets,
    DateTimeOffset? RecordedThrough,
    DateTimeOffset? LegacyRecordedThrough);

internal sealed record HourlyUsageState(
    int Version,
    HourlyUsageBucket[] Buckets,
    UsageHistoryEntry? LastSample,
    bool IsInitialized,
    bool UseLegacyHistory,
    DateTimeOffset? RecordedThrough = null);

internal sealed class AdaptiveWeeklyUsageStore
{
    // Kept for reading the previous reset-relative format; new observations use UTC hours.
    internal const int MaximumCompletedCycles = 8;
    internal const int BucketCount = 28;
    internal static readonly TimeSpan BucketDuration = TimeSpan.FromHours(6);
    internal const int RetentionDays = 56;
    internal const int MaximumHourlyBuckets = RetentionDays * 24 + 1;

    private const int SchemaVersion = 1;
    private const int MaximumDocumentBytes = 1024 * 1024;
    private const string FileName = "adaptive-weekly-forecast.json";
    private readonly string _legacyPath;
    private readonly AdaptiveWeeklyUsageState _legacy;
    private HourlyUsageState _state;
    private bool _unreadableHourlyFile;

    internal AdaptiveWeeklyUsageStore(string path)
    {
        _legacyPath = path;
        StoragePath = Path.ChangeExtension(path, ".hourly.json");
        _legacy = LoadLegacy();
        _state = Load();
        if (!_state.UseLegacyHistory && !_unreadableHourlyFile) StorageError = null;
    }

    internal static AdaptiveWeeklyUsageStore CreateDefault() => new(LocalStorage.GetPath(FileName));

    internal AdaptiveWeeklyUsageStore ForContext(string context) => new(LocalStorage.ContextPath(_legacyPath, context));

    internal string StoragePath { get; }

    internal string? StorageError { get; private set; }

    internal AdaptiveWeeklyUsageHistory Snapshot => new(
        _state.UseLegacyHistory ? _legacy.CompletedCycles.ToArray() : [],
        _state.UseLegacyHistory ? _legacy.ActiveCycle : null,
        new(_state.Buckets.ToArray(), _state.RecordedThrough ?? _state.LastSample?.RecordedAt,
            _state.UseLegacyHistory ? _legacy.LastSample?.RecordedAt : null));

    internal void Record(RateLimitWindow? window, IReadOnlyList<UsageHistoryEntry> weeklyHistory, TimeSpan maximumGap)
    {
        if (maximumGap <= TimeSpan.Zero) return;
        if (!TryGetSamples(window, weeklyHistory, out var samples))
        {
            BreakBaseline();
            return;
        }

        IEnumerable<UsageHistoryEntry> incoming = !_state.IsInitialized
            ? samples
            : _state.LastSample is { } last
                ? samples.Where(sample => sample.RecordedAt > last.RecordedAt)
                : samples.TakeLast(1);
        var buckets = _state.Buckets.ToDictionary(bucket => bucket.StartsAt);
        var previous = _state.LastSample;
        foreach (var current in incoming)
        {
            if (previous is not null && current.RecordedAt > previous.RecordedAt
                && current.RecordedAt - previous.RecordedAt <= maximumGap
                && !WeeklyAllowanceRestoration.IsDiscontinuity(previous, current))
            {
                AddInterval(buckets, previous, current);
            }

            previous = current;
        }

        _state = _state with
        {
            Buckets = RetainedBuckets(buckets.Values, previous?.RecordedAt ?? _state.RecordedThrough),
            LastSample = previous,
            RecordedThrough = previous?.RecordedAt ?? _state.RecordedThrough,
            IsInitialized = true,
        };
        Save(_state);
    }

    internal void AdvanceBaseline(RateLimitWindow? window, IReadOnlyList<UsageHistoryEntry> weeklyHistory)
    {
        if (!TryGetSamples(window, weeklyHistory, out var samples))
        {
            BreakBaseline();
            return;
        }
        var latest = samples.LastOrDefault();
        if (latest is not null && (_state.LastSample is null || latest.RecordedAt > _state.LastSample.RecordedAt))
        {
            _state = _state with { LastSample = latest };
        }

        _state = _state with
        {
            IsInitialized = true,
            RecordedThrough = _state.LastSample?.RecordedAt ?? _state.RecordedThrough,
            Buckets = RetainedBuckets(_state.Buckets, _state.LastSample?.RecordedAt ?? _state.RecordedThrough),
        };
        Save(_state);
    }

    internal bool Clear()
    {
        // The marker excludes both archived profiles and old raw chart observations after a restart.
        var cleared = new HourlyUsageState(SchemaVersion, [], null, true, false);
        if (!Save(cleared, allowRepair: true)) return false;
        _state = cleared;
        _unreadableHourlyFile = false;
        return true;
    }

    private static bool TryGetSamples(RateLimitWindow? window, IReadOnlyList<UsageHistoryEntry> history,
        out UsageHistoryEntry[] samples)
    {
        samples = [];
        if (window is not { WindowMinutes: 10080 } || window.ResetsAt < DateTimeOffset.MinValue.AddDays(7)) return false;
        samples = history.Where(sample => sample is not null && IsValidSample(sample)
                && (!sample.WindowMinutes.HasValue || sample.WindowMinutes == 10080)
                && sample.RecordedAt >= DateTimeOffset.MinValue.AddDays(RetentionDays)
                && sample.RecordedAt <= DateTimeOffset.MaxValue.AddHours(-1)
                && sample.RecordedAt <= (sample.ResetsAt ?? window.ResetsAt)
                && (!sample.ResetsAt.HasValue || sample.ResetsAt >= DateTimeOffset.MinValue.AddDays(7)
                    && sample.RecordedAt >= sample.ResetsAt.Value.AddDays(-7))
                && (sample.ResetsAt.HasValue || sample.RecordedAt >= window.ResetsAt.AddDays(-7)))
            .Select(sample => sample with { ResetsAt = sample.ResetsAt ?? window.ResetsAt, WindowMinutes = 10080 })
            .OrderBy(sample => sample.RecordedAt).DistinctBy(sample => sample.RecordedAt).ToArray();
        return true;
    }

    private static void AddInterval(Dictionary<DateTimeOffset, HourlyUsageBucket> buckets,
        UsageHistoryEntry previous, UsageHistoryEntry current)
    {
        var totalTicks = (current.RecordedAt - previous.RecordedAt).Ticks;
        var consumed = previous.RemainingPercent - current.RemainingPercent;
        var cursor = previous.RecordedAt.ToUniversalTime();
        while (cursor < current.RecordedAt)
        {
            var start = HourStart(cursor);
            var end = Min(current.RecordedAt, start.AddHours(1));
            var ticks = (end - cursor).Ticks;
            buckets.TryGetValue(start, out var existing);
            buckets[start] = new(start, (existing?.ObservedTicks ?? 0) + ticks,
                (existing?.ConsumedPercent ?? 0) + consumed * ((double)ticks / totalTicks));
            cursor = end;
        }
    }

    private HourlyUsageState Load()
    {
        if (!File.Exists(StoragePath))
        {
            // Do not replay chart/export observations: their historical learning-pause status is unknown.
            return new(SchemaVersion, [], _legacy.LastSample, _legacy.IsInitialized, true);
        }

        try
        {
            if (new FileInfo(StoragePath).Length > MaximumDocumentBytes) throw new JsonException("Hourly history is too large.");
            var state = JsonSerializer.Deserialize(File.ReadAllText(StoragePath), HourlyUsageJsonContext.Default.HourlyUsageState);
            var through = state?.RecordedThrough ?? state?.LastSample?.RecordedAt;
            if (state is null || state.Version != SchemaVersion || !state.IsInitialized || state.Buckets is null
                || state.Buckets.Length > MaximumHourlyBuckets
                || state.Buckets.Any(bucket => !IsValidBucket(bucket))
                || state.Buckets.Select(bucket => bucket.StartsAt).Distinct().Count() != state.Buckets.Length
                || state.LastSample is { } sample && (!IsValidSample(sample) || sample.WindowMinutes != 10080
                    || !sample.ResetsAt.HasValue || sample.RecordedAt > sample.ResetsAt
                    || sample.RecordedAt < DateTimeOffset.MinValue.AddDays(RetentionDays)
                    || sample.RecordedAt > DateTimeOffset.MaxValue.AddHours(-1)
                    || through.HasValue && sample.RecordedAt > through.Value)
                || through.HasValue && (through < DateTimeOffset.MinValue.AddDays(RetentionDays)
                    || through > DateTimeOffset.MaxValue.AddHours(-1))
                || state.Buckets.Length > 0 && (!through.HasValue
                    || state.Buckets.Any(bucket => bucket.StartsAt > through.Value
                        || bucket.ObservedTicks > (through.Value - bucket.StartsAt).Ticks)))
            {
                throw new JsonException("Invalid hourly forecast history.");
            }

            return state with { Buckets = state.Buckets.OrderBy(bucket => bucket.StartsAt).ToArray(), RecordedThrough = through };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            LocalStorage.TraceFailure("load hourly forecast history", error);
            _unreadableHourlyFile = true;
            StorageError = "Saved hourly forecast history could not be read. The file has been kept unchanged.";
            return new(SchemaVersion, [], null, true, false);
        }
    }

    private bool Save(HourlyUsageState state, bool allowRepair = false)
    {
        if (_unreadableHourlyFile && !allowRepair) return false;
        var saved = LocalStorage.TryWrite(StoragePath, JsonSerializer.Serialize(state, HourlyUsageJsonContext.Default.HourlyUsageState));
        StorageError = saved ? null : "Learned forecast history could not be saved. Please try again.";
        return saved;
    }

    private void BreakBaseline()
    {
        if (_state.LastSample is null) return;
        _state = _state with
        {
            RecordedThrough = _state.RecordedThrough ?? _state.LastSample.RecordedAt,
            LastSample = null,
            IsInitialized = true,
        };
        Save(_state);
    }

    private AdaptiveWeeklyUsageState LoadLegacy()
    {
        try
        {
            if (!File.Exists(_legacyPath)) return new([], null, null, false);
            var state = JsonSerializer.Deserialize(File.ReadAllText(_legacyPath), AdaptiveWeeklyUsageJsonContext.Default.AdaptiveWeeklyUsageState);
            if (state is null) return new([], null, null, true);
            var active = NormalizeCycle(state.ActiveCycle);
            var completed = new List<AdaptiveWeeklyUsageCycle>();
            foreach (var cycle in (state.CompletedCycles ?? []).Select(NormalizeCycle)
                .OfType<AdaptiveWeeklyUsageCycle>().OrderBy(cycle => cycle.ResetsAt))
            {
                if (active is not null && IsSameCycle(active, cycle.ResetsAt, cycle.WindowMinutes)) continue;
                if (completed.Count > 0 && IsSameCycle(completed[^1], cycle.ResetsAt, cycle.WindowMinutes))
                {
                    if (cycle.ObservedMinutes > completed[^1].ObservedMinutes) completed[^1] = cycle;
                }
                else completed.Add(cycle);
            }

            var last = state.LastSample is { } sample && active is not null && IsValidSample(sample)
                && (!sample.WindowMinutes.HasValue || sample.WindowMinutes == 10080)
                && sample.RecordedAt >= DateTimeOffset.MinValue.AddDays(RetentionDays)
                && sample.RecordedAt >= active.ResetsAt.AddDays(-7) && sample.RecordedAt <= active.ResetsAt
                && WeeklyAllowanceRestoration.IsInCycle(sample, active.ResetsAt, active.WindowMinutes)
                ? sample with { ResetsAt = sample.ResetsAt ?? active.ResetsAt, WindowMinutes = 10080 }
                : null;
            return new(completed.TakeLast(MaximumCompletedCycles).ToArray(), active, last, state.IsInitialized);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            LocalStorage.TraceFailure("load legacy forecast history", error);
            StorageError = "Saved forecast history could not be read. The original file has been kept.";
            return new([], null, null, true);
        }
    }

    private static AdaptiveWeeklyUsageCycle? NormalizeCycle(AdaptiveWeeklyUsageCycle? cycle)
    {
        if (cycle is null || cycle.WindowMinutes != 10080 || cycle.ResetsAt < DateTimeOffset.MinValue.AddDays(7)
            || !double.IsFinite(cycle.ObservedMinutes) || cycle.ObservedMinutes is < 0 or > 10080
            || !double.IsFinite(cycle.ConsumedPercent) || cycle.ConsumedPercent < 0) return null;
        var buckets = (cycle.Buckets ?? []).Where(bucket => bucket is not null && bucket.Index is >= 0 and < BucketCount
                && double.IsFinite(bucket.ObservedMinutes) && bucket.ObservedMinutes is >= 0 and <= 360.000001
                && double.IsFinite(bucket.ConsumedPercent) && bucket.ConsumedPercent >= 0)
            .GroupBy(bucket => bucket.Index)
            .Select(group => new AdaptiveWeeklyUsageBucket(group.Key, group.Sum(bucket => bucket.ObservedMinutes), group.Sum(bucket => bucket.ConsumedPercent)))
            .Where(bucket => bucket.ObservedMinutes <= 360.000001 && double.IsFinite(bucket.ConsumedPercent))
            .Select(bucket => bucket with { ObservedMinutes = Math.Min(360, bucket.ObservedMinutes) })
            .OrderBy(bucket => bucket.Index).ToArray();
        return cycle with { Buckets = buckets };
    }

    internal static bool IsSameCycle(AdaptiveWeeklyUsageCycle cycle, DateTimeOffset resetsAt, int windowMinutes) =>
        cycle.WindowMinutes == windowMinutes && (cycle.ResetsAt - resetsAt).Duration() <= WeeklyAllowanceRestoration.ResetCycleTolerance;

    internal static bool IsValidBucket(HourlyUsageBucket? bucket) => bucket is not null
        && bucket.StartsAt.Offset == TimeSpan.Zero && bucket.StartsAt.Ticks % TimeSpan.TicksPerHour == 0
        && bucket.StartsAt >= DateTimeOffset.MinValue.AddDays(RetentionDays)
        && bucket.ObservedTicks is > 0 and <= TimeSpan.TicksPerHour
        && double.IsFinite(bucket.ConsumedPercent) && bucket.ConsumedPercent >= 0;

    private static bool IsValidSample(UsageHistoryEntry sample) =>
        double.IsFinite(sample.RemainingPercent) && sample.RemainingPercent is >= 0 and <= 100;

    private static DateTimeOffset HourStart(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);

    private static HourlyUsageBucket[] RetainedBuckets(IEnumerable<HourlyUsageBucket> buckets, DateTimeOffset? through)
    {
        var cutoff = through is { } at ? HourStart(at).AddDays(-RetentionDays) : DateTimeOffset.MinValue;
        return buckets.Where(bucket => bucket.StartsAt >= cutoff).OrderBy(bucket => bucket.StartsAt)
            .TakeLast(MaximumHourlyBuckets).ToArray();
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}

[JsonSerializable(typeof(HourlyUsageState))]
internal sealed partial class HourlyUsageJsonContext : JsonSerializerContext
{
}
