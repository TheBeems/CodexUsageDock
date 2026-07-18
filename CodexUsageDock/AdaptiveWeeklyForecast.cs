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

internal sealed record AdaptiveWeeklyForecastProjection(UsageTrendForecast Forecast, string Status);

internal sealed class AdaptiveWeeklyUsageStore
{
    internal const int MaximumCompletedCycles = 8;
    internal const int BucketCount = 28;
    internal static readonly TimeSpan BucketDuration = TimeSpan.FromHours(6);

    private const string FileName = "adaptive-weekly-forecast.json";
    private readonly string _path;
    private AdaptiveWeeklyUsageState _state;

    internal AdaptiveWeeklyUsageStore(string path)
    {
        _path = path;
        _state = Load();
    }

    internal static AdaptiveWeeklyUsageStore CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageDock");
        return new AdaptiveWeeklyUsageStore(Path.Combine(directory, FileName));
    }

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

    internal void Clear()
    {
        // Keep only an empty initialization marker so old raw chart samples are not learnt again.
        _state = new AdaptiveWeeklyUsageState([], null, null, true);
        Save();
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
        if (active is not null && active.ResetsAt == window.ResetsAt && active.WindowMinutes == window.WindowMinutes)
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
            return new AdaptiveWeeklyUsageState([], null, null, false);
        }
    }

    private void Save()
    {
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state, AdaptiveWeeklyUsageJsonContext.Default.AdaptiveWeeklyUsageState));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static AdaptiveWeeklyUsageState Normalize(AdaptiveWeeklyUsageState? state)
    {
        if (state is null)
        {
            return new AdaptiveWeeklyUsageState([], null, null, false);
        }

        var completed = (state.CompletedCycles ?? [])
            .Select(NormalizeCycle)
            .Where(cycle => cycle is not null)
            .Cast<AdaptiveWeeklyUsageCycle>()
            .OrderBy(cycle => cycle.ResetsAt)
            .DistinctBy(cycle => cycle.ResetsAt)
            .TakeLast(MaximumCompletedCycles)
            .ToArray();
        var active = NormalizeCycle(state.ActiveCycle);
        var lastSample = active is not null && state.LastSample is { } sample && IsValidSample(sample)
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
            || cycle.ObservedMinutes < 0
            || cycle.ConsumedPercent < 0)
        {
            return null;
        }

        var buckets = (cycle.Buckets ?? [])
            .Where(bucket => bucket.Index is >= 0 and < BucketCount
                && double.IsFinite(bucket.ObservedMinutes)
                && double.IsFinite(bucket.ConsumedPercent)
                && bucket.ObservedMinutes >= 0
                && bucket.ConsumedPercent >= 0)
            .GroupBy(bucket => bucket.Index)
            .Select(group => new AdaptiveWeeklyUsageBucket(
                group.Key,
                group.Sum(bucket => bucket.ObservedMinutes),
                group.Sum(bucket => bucket.ConsumedPercent)))
            .OrderBy(bucket => bucket.Index)
            .ToArray();
        return cycle with { Buckets = buckets };
    }

    private static bool IsValidSample(UsageHistoryEntry sample) =>
        double.IsFinite(sample.RemainingPercent) && sample.RemainingPercent is >= 0 and <= 100;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}

internal static class AdaptiveWeeklyForecast
{
    private const double BootstrapWeight = 0.25;
    private const double MatureWeight = 0.35;
    private const int MinimumBucketCycles = 3;

    internal static AdaptiveWeeklyForecastProjection Project(
        UsageHistoryEntry latest,
        DateTimeOffset windowStart,
        DateTimeOffset resetsAt,
        double currentRatePerMinute,
        bool enabled,
        AdaptiveWeeklyUsageHistory? history)
    {
        if (!enabled || history is null)
        {
            return new AdaptiveWeeklyForecastProjection(
                ProjectAtCurrentRate(latest, resetsAt, currentRatePerMinute),
                "Forecast: current pace only.");
        }

        var completed = history.CompletedCycles
            .Where(cycle => cycle.WindowMinutes == (int)TimeSpan.FromDays(7).TotalMinutes && GetRate(cycle) is not null)
            .OrderByDescending(cycle => cycle.ResetsAt)
            .Take(AdaptiveWeeklyUsageStore.MaximumCompletedCycles)
            .ToArray();
        var provisional = history.ActiveCycle is { } active
            && active.ResetsAt == resetsAt
            && active.WindowMinutes == (int)TimeSpan.FromDays(7).TotalMinutes
            && GetRate(active) is not null
            ? active
            : null;
        var profiles = completed.Length > 0
            ? completed
            : provisional is null ? [] : [provisional];
        if (profiles.Length == 0)
        {
            return new AdaptiveWeeklyForecastProjection(
                ProjectAtCurrentRate(latest, resetsAt, currentRatePerMinute),
                "Forecast: current pace only.");
        }

        var historicalRate = Median(profiles.Select(cycle => GetRate(cycle)!.Value));
        var historicalWeight = completed.Length == 0
            ? BootstrapWeight
            : BootstrapWeight + (Math.Min(completed.Length, AdaptiveWeeklyUsageStore.MaximumCompletedCycles) - 1)
                * (MatureWeight - BootstrapWeight) / (AdaptiveWeeklyUsageStore.MaximumCompletedCycles - 1);
        var projected = ProjectByBucket(
            latest,
            windowStart,
            resetsAt,
            currentRatePerMinute,
            historicalRate,
            historicalWeight,
            profiles);
        var status = completed.Length == 0
            ? "Forecast: current pace + limited local history."
            : $"Forecast: current pace + local history ({completed.Length}/{AdaptiveWeeklyUsageStore.MaximumCompletedCycles} cycles).";
        return new AdaptiveWeeklyForecastProjection(projected, status);
    }

    private static UsageTrendForecast ProjectByBucket(
        UsageHistoryEntry latest,
        DateTimeOffset windowStart,
        DateTimeOffset resetsAt,
        double currentRatePerMinute,
        double historicalRatePerMinute,
        double historicalWeight,
        IReadOnlyList<AdaptiveWeeklyUsageCycle> profiles)
    {
        var points = new List<UsageTrendForecastPoint>();
        var cursor = latest.RecordedAt;
        var remaining = latest.RemainingPercent;
        while (cursor < resetsAt && remaining > 0)
        {
            var bucketIndex = GetBucketIndex(cursor, windowStart);
            var bucketEnd = Min(resetsAt, windowStart + TimeSpan.FromTicks(AdaptiveWeeklyUsageStore.BucketDuration.Ticks * (bucketIndex + 1)));
            if (bucketEnd <= cursor)
            {
                break;
            }

            var bucketFactor = GetBucketFactor(profiles, bucketIndex);
            var rate = currentRatePerMinute * (1 - historicalWeight) + historicalRatePerMinute * bucketFactor * historicalWeight;
            if (rate <= 0 || !double.IsFinite(rate))
            {
                return ProjectAtCurrentRate(latest, resetsAt, currentRatePerMinute);
            }

            var capacity = rate * (bucketEnd - cursor).TotalMinutes;
            if (capacity >= remaining)
            {
                var endsAt = cursor.AddMinutes(remaining / rate);
                points.Add(new UsageTrendForecastPoint(endsAt, 0));
                return new UsageTrendForecast(endsAt, 0, true, points);
            }

            remaining -= capacity;
            cursor = bucketEnd;
            points.Add(new UsageTrendForecastPoint(cursor, remaining));
        }

        return new UsageTrendForecast(resetsAt, Math.Max(0, remaining), false, points);
    }

    private static UsageTrendForecast ProjectAtCurrentRate(
        UsageHistoryEntry latest,
        DateTimeOffset resetsAt,
        double currentRatePerMinute)
    {
        var minutesToEmpty = latest.RemainingPercent / currentRatePerMinute;
        var estimated = latest.RecordedAt.AddMinutes(minutesToEmpty);
        if (estimated >= resetsAt)
        {
            var remainingAtReset = Math.Max(0, latest.RemainingPercent - currentRatePerMinute * (resetsAt - latest.RecordedAt).TotalMinutes);
            return new UsageTrendForecast(
                resetsAt,
                remainingAtReset,
                false,
                [new UsageTrendForecastPoint(resetsAt, remainingAtReset)]);
        }

        return new UsageTrendForecast(
            estimated,
            0,
            true,
            [new UsageTrendForecastPoint(estimated, 0)]);
    }

    private static double GetBucketFactor(IReadOnlyList<AdaptiveWeeklyUsageCycle> profiles, int bucketIndex)
    {
        var factors = profiles
            .Select(cycle =>
            {
                var rate = GetRate(cycle);
                var bucket = cycle.Buckets.FirstOrDefault(candidate => candidate.Index == bucketIndex);
                return rate is > 0 && bucket is { ObservedMinutes: > 0 }
                    ? bucket.ConsumedPercent / bucket.ObservedMinutes / rate.Value
                    : double.NaN;
            })
            .Where(double.IsFinite)
            .Where(factor => factor >= 0)
            .ToArray();
        return factors.Length >= MinimumBucketCycles
            ? Math.Clamp(Median(factors), 0, 4)
            : 1;
    }

    private static double? GetRate(AdaptiveWeeklyUsageCycle cycle) =>
        cycle.ObservedMinutes > 0 && double.IsFinite(cycle.ObservedMinutes) && double.IsFinite(cycle.ConsumedPercent)
            ? cycle.ConsumedPercent / cycle.ObservedMinutes
            : null;

    private static int GetBucketIndex(DateTimeOffset instant, DateTimeOffset windowStart) =>
        Math.Clamp((int)Math.Floor((instant - windowStart).TotalHours / AdaptiveWeeklyUsageStore.BucketDuration.TotalHours), 0, AdaptiveWeeklyUsageStore.BucketCount - 1);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}

[JsonSerializable(typeof(AdaptiveWeeklyUsageState))]
internal sealed partial class AdaptiveWeeklyUsageJsonContext : JsonSerializerContext
{
}
