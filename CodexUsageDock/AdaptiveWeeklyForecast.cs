using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsageDock;

internal sealed record AdaptiveWeeklyUsageBucket(int Index, double ObservedMinutes, double ConsumedPercent);

internal sealed record AdaptiveWeeklyUsageCycle(
    DateTimeOffset ResetsAt,
    int WindowMinutes,
    double ObservedMinutes,
    double ConsumedPercent,
    AdaptiveWeeklyUsageBucket[] Buckets);

internal sealed record AdaptiveWeeklyUsageState(
    AdaptiveWeeklyUsageCycle[] CompletedCycles,
    AdaptiveWeeklyUsageCycle? ActiveCycle,
    UsageHistoryEntry? LastSample,
    bool IsInitialized);

internal sealed record AdaptiveWeeklyUsageHistory(
    AdaptiveWeeklyUsageCycle[] CompletedCycles,
    AdaptiveWeeklyUsageCycle? ActiveCycle);

internal sealed record AdaptiveWeeklyForecastProjection(UsageTrendForecast Forecast, string Status, bool UsesHistory = false);

internal sealed class AdaptiveWeeklyUsageStore
{
    internal const int MaximumCompletedCycles = 8;
    internal const int BucketCount = 28;
    internal static readonly TimeSpan BucketDuration = TimeSpan.FromHours(6);

    // The app-server may report the same reset a few seconds apart on successive refreshes.
    private static readonly TimeSpan ResetTimeJitterTolerance = TimeSpan.FromMinutes(1);
    private const string FileName = "adaptive-weekly-forecast.json";
    private readonly string _path;
    private AdaptiveWeeklyUsageState _state;

    internal AdaptiveWeeklyUsageStore(string path)
    {
        _path = path;
        _state = Load();
    }

    internal static AdaptiveWeeklyUsageStore CreateDefault() => new(LocalStorage.GetPath(FileName));

    internal AdaptiveWeeklyUsageStore ForContext(string context) => new(LocalStorage.ContextPath(_path, context));

    internal string? StorageError { get; private set; }

    internal AdaptiveWeeklyUsageHistory Snapshot => new(
        _state.CompletedCycles.ToArray(),
        _state.ActiveCycle);

    internal void Record(
        RateLimitWindow? window,
        IReadOnlyList<UsageHistoryEntry> weeklyHistory,
        TimeSpan maximumGap)
    {
        if (maximumGap <= TimeSpan.Zero
            || !TryGetWindowSamples(window, weeklyHistory, out var samples))
        {
            return;
        }

        var replayHistory = !_state.IsInitialized;
        var hadActiveCycle = _state.ActiveCycle is not null;
        replayHistory |= StartCycleIfNeeded(window!) && hadActiveCycle;

        var replay = replayHistory
            ? samples
            : _state.LastSample is null
                ? samples.TakeLast(1)
                : samples.Where(sample => sample.RecordedAt > _state.LastSample.RecordedAt);
        foreach (var sample in replay)
        {
            ApplySample(sample, maximumGap);
        }

        Save();
    }

    internal void AdvanceBaseline(
        RateLimitWindow? window,
        IReadOnlyList<UsageHistoryEntry> weeklyHistory)
    {
        if (!TryGetWindowSamples(window, weeklyHistory, out var samples))
        {
            return;
        }

        _ = StartCycleIfNeeded(window!);
        var latest = samples.LastOrDefault();
        if (latest is not null && (_state.LastSample is null || latest.RecordedAt > _state.LastSample.RecordedAt))
        {
            _state = _state with { LastSample = latest, IsInitialized = true };
        }
        else
        {
            _state = _state with { IsInitialized = true };
        }

        Save();
    }

    internal bool Clear()
    {
        // Keep only an empty initialization marker so old raw chart samples are not learnt again.
        var cleared = new AdaptiveWeeklyUsageState([], null, null, true);
        if (!Save(cleared))
        {
            return false;
        }

        _state = cleared;
        return true;
    }

    private static bool TryGetWindowSamples(
        RateLimitWindow? window,
        IReadOnlyList<UsageHistoryEntry> weeklyHistory,
        out UsageHistoryEntry[] samples)
    {
        samples = [];
        if (window is null || window.WindowMinutes != (int)TimeSpan.FromDays(7).TotalMinutes)
        {
            return false;
        }

        var windowStart = window.ResetsAt - TimeSpan.FromMinutes(window.WindowMinutes);
        samples = weeklyHistory
            .Where(sample => sample.RecordedAt >= windowStart
                && sample.RecordedAt <= window.ResetsAt
                && IsValidSample(sample))
            .OrderBy(sample => sample.RecordedAt)
            .DistinctBy(sample => sample.RecordedAt)
            .ToArray();
        return true;
    }

    private bool StartCycleIfNeeded(RateLimitWindow window)
    {
        var active = _state.ActiveCycle;
        if (active is not null && IsSameCycle(active, window.ResetsAt, window.WindowMinutes))
        {
            return false;
        }

        var completed = _state.CompletedCycles;
        if (active is not null && active.ObservedMinutes > 0)
        {
            completed = completed
                .Append(active)
                .OrderBy(cycle => cycle.ResetsAt)
                .TakeLast(MaximumCompletedCycles)
                .ToArray();
        }

        _state = new AdaptiveWeeklyUsageState(completed, CreateCycle(window), null, true);
        return true;
    }

    private static AdaptiveWeeklyUsageCycle CreateCycle(RateLimitWindow window) => new(
        window.ResetsAt,
        window.WindowMinutes,
        0,
        0,
        []);

    private void ApplySample(UsageHistoryEntry sample, TimeSpan maximumGap)
    {
        var active = _state.ActiveCycle;
        if (active is null)
        {
            return;
        }

        var previous = _state.LastSample;
        if (previous is null || sample.RecordedAt <= previous.RecordedAt)
        {
            _state = _state with { LastSample = sample };
            return;
        }

        var interval = sample.RecordedAt - previous.RecordedAt;
        if (interval <= maximumGap && sample.RemainingPercent <= previous.RemainingPercent)
        {
            active = AddObservation(active, previous, sample);
        }

        _state = _state with { ActiveCycle = active, LastSample = sample };
    }

    private static AdaptiveWeeklyUsageCycle AddObservation(
        AdaptiveWeeklyUsageCycle cycle,
        UsageHistoryEntry previous,
        UsageHistoryEntry current)
    {
        var interval = current.RecordedAt - previous.RecordedAt;
        if (interval <= TimeSpan.Zero)
        {
            return cycle;
        }

        var cycleStart = cycle.ResetsAt - TimeSpan.FromMinutes(cycle.WindowMinutes);
        var buckets = cycle.Buckets.ToDictionary(bucket => bucket.Index);
        var consumed = Math.Max(0, previous.RemainingPercent - current.RemainingPercent);
        var segmentStart = previous.RecordedAt;
        while (segmentStart < current.RecordedAt)
        {
            var elapsed = segmentStart - cycleStart;
            var bucketIndex = Math.Clamp((int)Math.Floor(elapsed.TotalHours / BucketDuration.TotalHours), 0, BucketCount - 1);
            var bucketEnd = Min(
                current.RecordedAt,
                cycleStart + TimeSpan.FromTicks(BucketDuration.Ticks * (bucketIndex + 1)));
            var duration = bucketEnd - segmentStart;
            if (duration <= TimeSpan.Zero)
            {
                break;
            }

            var share = duration.TotalMilliseconds / interval.TotalMilliseconds;
            buckets.TryGetValue(bucketIndex, out var existing);
            buckets[bucketIndex] = new AdaptiveWeeklyUsageBucket(
                bucketIndex,
                (existing?.ObservedMinutes ?? 0) + duration.TotalMinutes,
                (existing?.ConsumedPercent ?? 0) + consumed * share);
            segmentStart = bucketEnd;
        }

        return cycle with
        {
            ObservedMinutes = cycle.ObservedMinutes + interval.TotalMinutes,
            ConsumedPercent = cycle.ConsumedPercent + consumed,
            Buckets = buckets.Values.OrderBy(bucket => bucket.Index).ToArray(),
        };
    }

    private AdaptiveWeeklyUsageState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AdaptiveWeeklyUsageState([], null, null, false);
            }

            var state = JsonSerializer.Deserialize(File.ReadAllText(_path), AdaptiveWeeklyUsageJsonContext.Default.AdaptiveWeeklyUsageState);
            return Normalize(state);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            LocalStorage.TraceFailure("load forecast history", error);
            StorageError = "Saved forecast history could not be read. A new history will be collected.";
            return new AdaptiveWeeklyUsageState([], null, null, false);
        }
    }

    private bool Save(AdaptiveWeeklyUsageState? state = null)
    {
        var saved = LocalStorage.TryWrite(_path, JsonSerializer.Serialize(state ?? _state, AdaptiveWeeklyUsageJsonContext.Default.AdaptiveWeeklyUsageState));
        StorageError = saved ? null : "Learned forecast history could not be saved. Please try again.";
        return saved;
    }
    private static AdaptiveWeeklyUsageState Normalize(AdaptiveWeeklyUsageState? state)
    {
        if (state is null)
        {
            return new AdaptiveWeeklyUsageState([], null, null, false);
        }

        var active = NormalizeCycle(state.ActiveCycle);
        var completed = NormalizeCompletedCycles((state.CompletedCycles ?? [])
            .Select(NormalizeCycle)
            .Where(cycle => cycle is not null)
            .Cast<AdaptiveWeeklyUsageCycle>()
            .ToArray(), active);
        var lastSample = active is not null && state.LastSample is { } sample && IsValidSample(sample)
            && sample.RecordedAt >= active.ResetsAt.AddMinutes(-active.WindowMinutes)
            && sample.RecordedAt <= active.ResetsAt
            ? sample
            : null;
        return new AdaptiveWeeklyUsageState(completed, active, lastSample, state.IsInitialized);
    }

    private static AdaptiveWeeklyUsageCycle? NormalizeCycle(AdaptiveWeeklyUsageCycle? cycle)
    {
        if (cycle is null
            || cycle.WindowMinutes != (int)TimeSpan.FromDays(7).TotalMinutes
            || !double.IsFinite(cycle.ObservedMinutes)
            || !double.IsFinite(cycle.ConsumedPercent)
            || cycle.ResetsAt < DateTimeOffset.MinValue.AddDays(7)
            || cycle.ObservedMinutes > cycle.WindowMinutes
            || cycle.ObservedMinutes < 0
            || cycle.ConsumedPercent < 0)
        {
            return null;
        }

        var buckets = (cycle.Buckets ?? [])
            .Where(bucket => bucket is not null && bucket.Index is >= 0 and < BucketCount
                && double.IsFinite(bucket.ObservedMinutes)
                && double.IsFinite(bucket.ConsumedPercent)
                && bucket.ObservedMinutes >= 0
                && bucket.ObservedMinutes <= BucketDuration.TotalMinutes
                && bucket.ConsumedPercent >= 0)
            .GroupBy(bucket => bucket.Index)
            .Select(group => new AdaptiveWeeklyUsageBucket(
                group.Key,
                group.Sum(bucket => bucket.ObservedMinutes),
                group.Sum(bucket => bucket.ConsumedPercent)))
            .Where(bucket => bucket.ObservedMinutes <= BucketDuration.TotalMinutes
                && double.IsFinite(bucket.ConsumedPercent))
            .OrderBy(bucket => bucket.Index)
            .ToArray();
        return cycle with { Buckets = buckets };
    }

    private static AdaptiveWeeklyUsageCycle[] NormalizeCompletedCycles(
        IEnumerable<AdaptiveWeeklyUsageCycle> cycles,
        AdaptiveWeeklyUsageCycle? active)
    {
        var completed = new List<AdaptiveWeeklyUsageCycle>();
        foreach (var cycle in cycles.OrderBy(cycle => cycle.ResetsAt))
        {
            if (active is not null && IsSameCycle(cycle, active.ResetsAt, active.WindowMinutes))
            {
                continue;
            }

            if (completed.Count > 0 && IsSameCycle(cycle, completed[^1].ResetsAt, completed[^1].WindowMinutes))
            {
                if (cycle.ObservedMinutes > completed[^1].ObservedMinutes)
                {
                    completed[^1] = cycle;
                }

                continue;
            }

            completed.Add(cycle);
        }

        return completed.TakeLast(MaximumCompletedCycles).ToArray();
    }

    internal static bool IsSameCycle(
        AdaptiveWeeklyUsageCycle cycle,
        DateTimeOffset resetsAt,
        int windowMinutes) =>
        cycle.WindowMinutes == windowMinutes
        && Math.Abs((cycle.ResetsAt - resetsAt).TotalSeconds) < ResetTimeJitterTolerance.TotalSeconds;

    private static bool IsValidSample(UsageHistoryEntry sample) =>
        double.IsFinite(sample.RemainingPercent) && sample.RemainingPercent is >= 0 and <= 100;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}

internal static class AdaptiveWeeklyForecast
{
    private const int MinimumPatternCycles = 3;
    private const double MinimumCoverage = 0.5;
    private static readonly TimeSpan MaximumHistoryAge = TimeSpan.FromDays(56);
    private static readonly TimeSpan RecentInfluenceDuration = TimeSpan.FromHours(3);

    internal static AdaptiveWeeklyForecastProjection Project(
        UsageHistoryEntry latest,
        DateTimeOffset windowStart,
        DateTimeOffset resetsAt,
        double currentRatePerMinute,
        bool enabled,
        AdaptiveWeeklyUsageHistory? history,
        TimeSpan? recentDuration = null)
    {
        var basis = recentDuration is { } duration
            ? duration.TotalHours < 1 ? $"recent {duration.TotalMinutes:0} min" : $"recent {duration.TotalHours:0.#} h"
            : "current pace";
        var profiles = enabled && history is not null
            ? history.CompletedCycles
                .Where(cycle => IsRepresentative(cycle)
                    && cycle.ResetsAt <= windowStart.AddMinutes(1)
                    && windowStart - cycle.ResetsAt <= MaximumHistoryAge)
                .OrderByDescending(cycle => cycle.ResetsAt)
                .DistinctBy(cycle => cycle.ResetsAt)
                .Take(AdaptiveWeeklyUsageStore.MaximumCompletedCycles)
                .ToArray()
            : [];
        if (profiles.Length == 0)
        {
            var reason = enabled ? " Weekly history is insufficient or outdated." : string.Empty;
            return new(ProjectAtCurrentRate(latest, resetsAt, currentRatePerMinute),
                $"Forecast: {basis} only.{reason}");
        }

        var totalWeight = profiles.Sum(cycle => Weight(cycle, windowStart));
        var coverage = profiles.Sum(cycle => Coverage(cycle) * Weight(cycle, windowStart)) / totalWeight;
        // Limited coverage or history depth slows the transition from recent pace to the historical baseline.
        var historyInfluence = coverage * Math.Min(1, totalWeight / MinimumPatternCycles);
        var historicalRate = WeightedMedian(profiles.Select(cycle =>
            (Rate: cycle.ConsumedPercent / cycle.ObservedMinutes, Weight: Weight(cycle, windowStart))));
        var points = new List<UsageTrendForecastPoint>();
        var cursor = latest.RecordedAt;
        var remaining = latest.RemainingPercent;
        while (cursor < resetsAt && remaining > 0)
        {
            var index = GetBucketIndex(cursor, windowStart);
            var end = Min(resetsAt, Min(cursor.AddHours(1),
                windowStart + TimeSpan.FromTicks(AdaptiveWeeklyUsageStore.BucketDuration.Ticks * (index + 1))));
            var horizon = (cursor - latest.RecordedAt + (end - cursor) / 2).TotalMinutes;
            var historyWeight = 1 - Math.Exp(-horizon * historyInfluence / RecentInfluenceDuration.TotalMinutes);
            var bucketRate = BucketRate(profiles, index, windowStart) ?? historicalRate;
            var rate = currentRatePerMinute * (1 - historyWeight) + bucketRate * historyWeight;
            var consumed = rate * (end - cursor).TotalMinutes;
            if (consumed >= remaining && rate > 0)
            {
                var limit = cursor.AddMinutes(remaining / rate);
                points.Add(new(limit, 0));
                return Result(new(limit, 0, limit < resetsAt, points));
            }

            remaining = Math.Max(0, remaining - consumed);
            cursor = end;
            points.Add(new(cursor, remaining));
        }

        return Result(new(resetsAt, remaining, false, points));

        AdaptiveWeeklyForecastProjection Result(UsageTrendForecast forecast) => new(forecast,
            $"Forecast: {basis} + {profiles.Length} usable week{(profiles.Length == 1 ? string.Empty : "s")} ({coverage:P0} observed). Historical influence grows further ahead; estimate only.",
            UsesHistory: true);
    }

    private static bool IsRepresentative(AdaptiveWeeklyUsageCycle cycle) =>
        cycle.WindowMinutes == 10080
        && double.IsFinite(cycle.ObservedMinutes) && cycle.ObservedMinutes is >= 5040 and <= 10080
        && double.IsFinite(cycle.ConsumedPercent) && cycle.ConsumedPercent >= 0
        && cycle.Buckets.All(bucket => bucket.Index is >= 0 and < 28
            && double.IsFinite(bucket.ObservedMinutes) && bucket.ObservedMinutes is >= 0 and <= 360
            && double.IsFinite(bucket.ConsumedPercent) && bucket.ConsumedPercent >= 0)
        && cycle.Buckets.Select(bucket => bucket.Index).Distinct().Count() == cycle.Buckets.Length
        && Math.Abs(cycle.Buckets.Sum(bucket => bucket.ObservedMinutes) - cycle.ObservedMinutes) < 0.001
        && Math.Abs(cycle.Buckets.Sum(bucket => bucket.ConsumedPercent) - cycle.ConsumedPercent) < 0.001
        && Coverage(cycle) >= MinimumCoverage
        // Observing only daytime activity cannot establish an around-the-clock baseline.
        && Enumerable.Range(0, 4).All(slot => cycle.Buckets
            .Where(bucket => bucket.Index % 4 == slot).Sum(bucket => bucket.ObservedMinutes) >= 360);

    private static double Coverage(AdaptiveWeeklyUsageCycle cycle) =>
        Math.Min(cycle.ObservedMinutes, cycle.Buckets.Sum(bucket => bucket.ObservedMinutes)) / cycle.WindowMinutes;

    private static double Weight(AdaptiveWeeklyUsageCycle cycle, DateTimeOffset windowStart) =>
        Coverage(cycle) * Math.Pow(0.5, Math.Max(0, (windowStart - cycle.ResetsAt).TotalDays) / 28);

    private static double? BucketRate(AdaptiveWeeklyUsageCycle[] profiles, int index, DateTimeOffset windowStart)
    {
        var rates = profiles.Where(cycle => HasMatchingCalendarPhase(cycle, windowStart))
            .Select(cycle => (Cycle: cycle, Bucket: cycle.Buckets.FirstOrDefault(bucket => bucket.Index == index)))
            .Where(item => item.Bucket is { ObservedMinutes: >= 180 })
            .Select(item => (Rate: item.Bucket!.ConsumedPercent / item.Bucket.ObservedMinutes,
                Weight: Weight(item.Cycle, windowStart) * item.Bucket.ObservedMinutes / 360))
            .ToArray();
        return rates.Length >= MinimumPatternCycles ? WeightedMedian(rates) : null;
    }

    private static bool HasMatchingCalendarPhase(AdaptiveWeeklyUsageCycle cycle, DateTimeOffset windowStart)
    {
        var previous = cycle.ResetsAt.AddMinutes(-cycle.WindowMinutes).ToLocalTime();
        var current = windowStart.ToLocalTime();
        // Older files store reset-relative buckets. Do not reinterpret shifted resets or DST as the same clock-time pattern.
        return previous.DayOfWeek == current.DayOfWeek && previous.Offset == current.Offset
            && Math.Abs((previous.TimeOfDay - current.TimeOfDay).TotalMinutes) < 1;
    }

    private static int GetBucketIndex(DateTimeOffset instant, DateTimeOffset windowStart) =>
        Math.Clamp((int)((instant - windowStart).TotalHours / 6), 0, 27);

    private static double WeightedMedian(IEnumerable<(double Rate, double Weight)> values)
    {
        var ordered = values.OrderBy(value => value.Rate).ToArray();
        var half = ordered.Sum(value => value.Weight) / 2;
        var cumulative = 0d;
        foreach (var value in ordered)
        {
            cumulative += value.Weight;
            if (cumulative >= half)
            {
                return value.Rate;
            }
        }

        return ordered[^1].Rate;
    }

    private static UsageTrendForecast ProjectAtCurrentRate(UsageHistoryEntry latest, DateTimeOffset resetsAt, double rate)
    {
        var remainingAtReset = latest.RemainingPercent - rate * (resetsAt - latest.RecordedAt).TotalMinutes;
        if (rate <= 0 || remainingAtReset >= 0)
        {
            return new(resetsAt, Math.Max(0, remainingAtReset), false, [new(resetsAt, Math.Max(0, remainingAtReset))]);
        }

        var limit = latest.RecordedAt.AddMinutes(latest.RemainingPercent / rate);
        return new(limit, 0, true, [new(limit, 0)]);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}
[JsonSerializable(typeof(AdaptiveWeeklyUsageState))]
internal sealed partial class AdaptiveWeeklyUsageJsonContext : JsonSerializerContext
{
}
