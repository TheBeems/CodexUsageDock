using System.Globalization;

namespace CodexUsageDock;

internal sealed record UsageAlertOptions(
    bool Enabled = false,
    int LowRemainingPercent = 10,
    bool ForecastWarnings = true,
    bool ResetExpiryWarnings = true);

internal sealed record UsageAlert(string Key, string Message);

internal sealed class UsageAlertEvaluator
{
    private const int MaximumTrackedWindows = 32;
    private const int MaximumTrackedResetExpiries = 32;
    private static readonly TimeSpan WindowCycleTolerance = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ForecastWarningHorizon = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan ResetExpiryWarningHorizon = TimeSpan.FromHours(24);
    private readonly object _stateLock = new();
    private UsageAlertOptions? _lastOptions;
    private string? _activeAccountKey;
    private AccountAlertState? _state;

    internal IReadOnlyList<UsageAlert> Evaluate(
        UsagePresentation presentation,
        DateTimeOffset now,
        TimeSpan refreshInterval,
        UsageAlertOptions options)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(options);

        lock (_stateLock)
        {
            if (!options.Enabled)
            {
                ClearState();
                _lastOptions = options;
                return [];
            }

            if (_lastOptions is null || !_lastOptions.Equals(options))
            {
                ClearState();
                _lastOptions = options;
            }

            var snapshot = presentation.Usage;
            if (!IsEligible(presentation, now, refreshInterval, snapshot))
            {
                return [];
            }

            var accountKey = snapshot.AccountKey!.Trim();
            if (!string.Equals(_activeAccountKey, accountKey, StringComparison.Ordinal))
            {
                ClearState();
                _activeAccountKey = accountKey;
            }

            var category = GetDefaultCategory(snapshot);
            if (_state is null || !string.Equals(_state.DefaultCategory, category, StringComparison.Ordinal))
            {
                _state = new AccountAlertState(category);
            }

            var alerts = new List<UsageAlert>();
            var observations = CollectWindowObservations(snapshot, now);
            var currentWindowKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observation in observations)
            {
                var key = GetWindowKey(observation);
                currentWindowKeys.Add(key);
                var windowState = GetWindowState(observation);

                EvaluateLowRemaining(observation, windowState, options, alerts);
            }

            foreach (var key in _state.Windows.Keys.Where(key => !currentWindowKeys.Contains(key)).ToArray())
            {
                _state.Windows.Remove(key);
            }

            if (options.ForecastWarnings)
            {
                EvaluateForecastWarning(
                    observations,
                    snapshot.Primary,
                    presentation.PrimaryHistory,
                    now,
                    refreshInterval,
                    alerts);
                EvaluateForecastWarning(
                    observations,
                    snapshot.Secondary,
                    presentation.WeeklyHistory,
                    now,
                    refreshInterval,
                    alerts);
            }

            if (options.ResetExpiryWarnings)
            {
                EvaluateResetExpiry(snapshot.ResetCredits, now, alerts);
            }
            else
            {
                _state.ResetExpiries.Clear();
            }

            return alerts;
        }
    }

    internal void Reset()
    {
        lock (_stateLock)
        {
            ClearState();
            _lastOptions = null;
        }
    }

    private static bool IsEligible(
        UsagePresentation presentation,
        DateTimeOffset now,
        TimeSpan refreshInterval,
        CodexUsageSnapshot snapshot) =>
        !presentation.IsLoading
        && snapshot.Source == UsageDataSource.AppServer
        && !string.IsNullOrWhiteSpace(snapshot.AccountKey)
        && UsageFreshness.IsFresh(snapshot.UpdatedAt, now, refreshInterval);

    private static string GetDefaultCategory(CodexUsageSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.DefaultBucketId) ? "default" : snapshot.DefaultBucketId!;

    private static List<WindowObservation> CollectWindowObservations(
        CodexUsageSnapshot snapshot,
        DateTimeOffset now)
    {
        var observations = new List<WindowObservation>(MaximumTrackedWindows);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var defaultCategory = GetDefaultCategory(snapshot);
        AddObservation(observations, seen, defaultCategory, "default", "primary", snapshot.Primary, now, isDefault: true, knownDefaultWindow: null);
        AddObservation(observations, seen, defaultCategory, "default", "secondary", snapshot.Secondary, now, isDefault: true, knownDefaultWindow: null);

        if (snapshot.Buckets is not { Count: > 0 } buckets)
        {
            return observations;
        }

        for (var index = 0; index < buckets.Count && observations.Count < MaximumTrackedWindows; index++)
        {
            var bucket = buckets[index];
            if (bucket is null)
            {
                continue;
            }

            var isDefaultBucket = string.Equals(bucket.Id, snapshot.DefaultBucketId, StringComparison.Ordinal);
            var category = isDefaultBucket
                ? defaultCategory
                : string.IsNullOrWhiteSpace(bucket.Id) ? $"bucket-{index + 1}" : bucket.Id;
            var label = isDefaultBucket
                ? "default"
                : UsageText.SanitizeExternal(bucket.Name, 70)
                    ?? UsageText.SanitizeExternal(bucket.Id, 70)
                    ?? $"Additional quota {index + 1}";
            AddObservation(
                observations,
                seen,
                category,
                label,
                "primary",
                bucket.Primary,
                now,
                isDefault: isDefaultBucket,
                knownDefaultWindow: snapshot.Primary);
            AddObservation(
                observations,
                seen,
                category,
                label,
                "secondary",
                bucket.Secondary,
                now,
                isDefault: isDefaultBucket,
                knownDefaultWindow: snapshot.Secondary);
        }

        return observations;
    }

    private static void AddObservation(
        List<WindowObservation> observations,
        HashSet<string> seen,
        string category,
        string label,
        string role,
        RateLimitWindow? window,
        DateTimeOffset now,
        bool isDefault,
        RateLimitWindow? knownDefaultWindow)
    {
        if (window is null
            || observations.Count >= MaximumTrackedWindows
            || isDefault && window == knownDefaultWindow
            || !UsageFreshness.IsValidWindow(window, now))
        {
            return;
        }

        var observationKey = $"{category}|{role}|{window.WindowMinutes}|{window.ResetsAt.UtcTicks}|{window.UsedPercent.ToString("R", CultureInfo.InvariantCulture)}";
        if (!seen.Add(observationKey))
        {
            return;
        }

        var identityKey = $"{category}|{role}|{window.WindowMinutes}";
        observations.Add(new WindowObservation(category, label, role, window, isDefault, identityKey));
    }

    private WindowAlertState GetWindowState(WindowObservation observation)
    {
        var key = GetWindowKey(observation);
        if (_state!.Windows.TryGetValue(key, out var state))
        {
            var delta = state.CycleResetAt >= observation.Window.ResetsAt
                ? state.CycleResetAt - observation.Window.ResetsAt
                : observation.Window.ResetsAt - state.CycleResetAt;
            if (delta <= WindowCycleTolerance)
            {
                state.CycleResetAt = observation.Window.ResetsAt;
                return state;
            }
        }

        state = new WindowAlertState(observation.Window.ResetsAt);
        _state.Windows[key] = state;
        return state;
    }

    private static void EvaluateLowRemaining(
        WindowObservation observation,
        WindowAlertState state,
        UsageAlertOptions options,
        List<UsageAlert> alerts)
    {
        var remaining = observation.Window.RemainingPercent;
        var threshold = Math.Clamp(options.LowRemainingPercent, 0, 100);
        if (!state.HasMeasurement)
        {
            state.HasMeasurement = true;
            state.LastRemaining = remaining;
            state.Rearmed = remaining > threshold;
            return;
        }

        var crossedDown = state.Rearmed
            && state.LastRemaining > threshold
            && remaining <= threshold;
        if (crossedDown)
        {
            alerts.Add(new UsageAlert(
                $"low:{GetWindowKey(observation)}:{state.CycleKey}",
                $"{FormatWindowName(observation)} is at {FormatPercent(remaining)}% remaining."));
            state.Rearmed = false;
        }

        if (remaining > threshold + 5)
        {
            state.Rearmed = true;
        }

        state.LastRemaining = remaining;
    }

    private void EvaluateForecastWarning(
        IReadOnlyList<WindowObservation> observations,
        RateLimitWindow? targetWindow,
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset now,
        TimeSpan refreshInterval,
        List<UsageAlert> alerts)
    {
        if (targetWindow is null)
        {
            return;
        }

        var observation = observations.FirstOrDefault(candidate =>
            candidate.IsDefault && candidate.Window == targetWindow);
        if (observation is null || !_state!.Windows.TryGetValue(GetWindowKey(observation), out var state))
        {
            return;
        }

        UsageTrendForecast? forecast = null;
        try
        {
            var windowStart = targetWindow.ResetsAt - TimeSpan.FromMinutes(targetWindow.WindowMinutes);
            forecast = UsageTrendAnalyzer.Analyze(
                history,
                windowStart,
                targetWindow.ResetsAt,
                now,
                dataAvailable: true,
                UsageFreshness.MaximumAge(refreshInterval)).Forecast;
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (OverflowException)
        {
        }

        var atRisk = forecast is { ReachesLimitBeforeReset: true } candidate
            && candidate.EndsAt >= now
            && candidate.EndsAt < targetWindow.ResetsAt
            && candidate.EndsAt - now <= ForecastWarningHorizon;
        if (!state.ForecastInitialized)
        {
            state.ForecastInitialized = true;
            state.ForecastAtRisk = atRisk;
            return;
        }

        if (atRisk && !state.ForecastAtRisk && !state.ForecastWarningIssued)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((forecast!.EndsAt - now).TotalMinutes));
            alerts.Add(new UsageAlert(
                $"forecast:{GetWindowKey(observation)}:{state.CycleKey}",
                $"{FormatWindowName(observation)} forecast may reach its limit within {minutes.ToString(CultureInfo.InvariantCulture)} minutes."));
            state.ForecastWarningIssued = true;
        }

        state.ForecastAtRisk = atRisk;
    }

    private void EvaluateResetExpiry(
        RateLimitResetCredits? resets,
        DateTimeOffset now,
        List<UsageAlert> alerts)
    {
        if (resets is not { AvailableCount: > 0, Credits: { Count: > 0 } credits })
        {
            _state!.ResetExpiries.Clear();
            return;
        }

        var eligible = credits
            .Where(credit => credit is not null
                && credit.ExpiresAt is { } expiry
                && expiry > now
                && IsAvailableStatus(credit.Status))
            .Select(credit => credit.ExpiresAt!.Value)
            .GroupBy(expiry => expiry.UtcTicks)
            .OrderBy(group => group.Key)
            .Take(MaximumTrackedResetExpiries)
            .Select(group => group.First())
            .ToArray();
        var currentKeys = new HashSet<long>();
        foreach (var expiry in eligible)
        {
            var key = expiry.UtcTicks;
            currentKeys.Add(key);
            var withinWarningWindow = expiry - now <= ResetExpiryWarningHorizon;
            if (!_state!.ResetExpiries.TryGetValue(key, out var state))
            {
                _state.ResetExpiries[key] = new ResetExpiryState(withinWarningWindow);
                continue;
            }

            if (withinWarningWindow && !state.WithinWarningWindow)
            {
                alerts.Add(new UsageAlert(
                    $"reset-expiry:{key.ToString(CultureInfo.InvariantCulture)}",
                    $"A reset credit expires within 24 hours at {expiry.ToUniversalTime():yyyy-MM-dd HH:mm} UTC."));
            }

            state.WithinWarningWindow = withinWarningWindow;
        }

        foreach (var key in _state!.ResetExpiries.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            _state.ResetExpiries.Remove(key);
        }
    }

    private static bool IsAvailableStatus(string? status) =>
        status is null || string.Equals(status.Trim(), "available", StringComparison.OrdinalIgnoreCase);

    private static string GetWindowKey(WindowObservation observation) => observation.Key;

    private static string FormatWindowName(WindowObservation observation)
    {
        var duration = FormatWindowDuration(observation.Window.WindowMinutes);
        return observation.IsDefault
            ? $"{duration} window"
            : $"{observation.Label} {observation.Role} window ({duration})";
    }

    private static string FormatWindowDuration(int minutes)
    {
        if (minutes == (int)TimeSpan.FromHours(5).TotalMinutes)
        {
            return "5-hour";
        }

        if (minutes == (int)TimeSpan.FromDays(7).TotalMinutes)
        {
            return "weekly";
        }

        if (minutes > 0 && minutes % (int)TimeSpan.FromDays(1).TotalMinutes == 0)
        {
            return $"{minutes / (int)TimeSpan.FromDays(1).TotalMinutes}-day";
        }

        if (minutes > 0 && minutes % 60 == 0)
        {
            return $"{minutes / 60}-hour";
        }

        return $"{minutes}-minute";
    }

    private static string FormatPercent(double remaining) =>
        remaining.ToString("0", CultureInfo.InvariantCulture);

    private void ClearState()
    {
        _activeAccountKey = null;
        _state = null;
    }

    private sealed record WindowObservation(
        string Category,
        string Label,
        string Role,
        RateLimitWindow Window,
        bool IsDefault,
        string Key);

    private sealed class WindowAlertState
    {
        internal WindowAlertState(DateTimeOffset cycleResetAt)
        {
            CycleResetAt = cycleResetAt;
            CycleKey = cycleResetAt.UtcTicks.ToString(CultureInfo.InvariantCulture);
        }

        internal DateTimeOffset CycleResetAt { get; set; }
        internal string CycleKey { get; }
        internal bool HasMeasurement { get; set; }
        internal double LastRemaining { get; set; }
        internal bool Rearmed { get; set; }
        internal bool ForecastAtRisk { get; set; }
        internal bool ForecastInitialized { get; set; }
        internal bool ForecastWarningIssued { get; set; }
    }

    private sealed class ResetExpiryState(bool withinWarningWindow)
    {
        internal bool WithinWarningWindow { get; set; } = withinWarningWindow;
    }

    private sealed class AccountAlertState(string defaultCategory)
    {
        internal string DefaultCategory { get; } = defaultCategory;
        internal Dictionary<string, WindowAlertState> Windows { get; } = new(StringComparer.Ordinal);
        internal Dictionary<long, ResetExpiryState> ResetExpiries { get; } = [];
    }
}
