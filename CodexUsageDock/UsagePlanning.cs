using System.Globalization;

namespace CodexUsageDock;

internal static class UsagePlanner
{
    internal const string Disclaimer = "Planning guidance only; it is not a guarantee.";

    internal static UsagePlanningResult Plan(
        UsagePresentation presentation,
        DateTimeOffset now,
        TimeSpan refreshInterval,
        DateTimeOffset desiredEnd,
        int? remainingWorkdays = null,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        if (desiredEnd <= now)
        {
            return Unavailable(desiredEnd, "Planning unavailable: the desired end must be after now.", remainingWorkdays);
        }

        if (remainingWorkdays is < 1 or > 7)
        {
            return Unavailable(
                desiredEnd,
                "Planning unavailable: remaining workdays must be between 1 and 7.",
                remainingWorkdays);
        }

        var snapshot = presentation.Usage;
        if (presentation.IsLoading)
        {
            return Unavailable(desiredEnd, "Planning paused while fresh usage data is loading.", remainingWorkdays);
        }

        if (snapshot.Source != UsageDataSource.AppServer)
        {
            var sourceMessage = snapshot.Source switch
            {
                UsageDataSource.LastConfirmed => "Planning paused: only last confirmed usage is available; refresh live usage first.",
                UsageDataSource.Unavailable => "Planning paused: live usage is unavailable.",
                UsageDataSource.LocalSession => "Planning paused: local session usage is not a verified live app-server measurement.",
                _ => "Planning paused until fresh live app-server usage is available.",
            };
            return Unavailable(desiredEnd, sourceMessage, remainingWorkdays);
        }

        if (string.IsNullOrWhiteSpace(snapshot.AccountKey))
        {
            return Unavailable(desiredEnd, "Planning paused: the verified account identity is unavailable.", remainingWorkdays);
        }

        var freshness = UsageFreshness.Classify(snapshot.UpdatedAt, now, refreshInterval);
        if (freshness != UsageFreshnessState.Fresh)
        {
            var freshnessMessage = freshness switch
            {
                UsageFreshnessState.Stale => "Planning paused: usage data is stale; refresh before planning.",
                UsageFreshnessState.Future => "Planning paused: the usage timestamp is in the future.",
                UsageFreshnessState.Unknown => "Planning paused: usage freshness is unknown.",
                _ => "Planning paused until fresh usage data is available.",
            };
            return Unavailable(desiredEnd, freshnessMessage, remainingWorkdays);
        }

        if (snapshot.OrdinaryUsageAllowed == false)
        {
            return Unavailable(desiredEnd, "Planning paused: ordinary usage is currently blocked.", remainingWorkdays);
        }

        try
        {
            var maximumSampleAge = UsageFreshness.MaximumAge(refreshInterval);
            var primary = CreateWindowPlan(
                snapshot.Primary,
                presentation.PrimaryHistory,
                now,
                desiredEnd,
                remainingWorkdays,
                maximumSampleAge,
                adaptiveCycleCount: 0,
                isPrimary: true,
                timeZone ?? TimeZoneInfo.Local);
            var weekly = CreateWindowPlan(
                snapshot.Secondary,
                presentation.WeeklyHistory,
                now,
                desiredEnd,
                remainingWorkdays,
                maximumSampleAge,
                CountAdaptiveCycles(presentation),
                isPrimary: false,
                timeZone ?? TimeZoneInfo.Local);

            if (primary is null && weekly is null)
            {
                return Unavailable(
                    desiredEnd,
                    "Planning paused: no valid 5-hour or weekly allowance window is available.",
                    remainingWorkdays);
            }

            var windows = new[] { primary, weekly }.Where(window => window is not null).Cast<UsagePlanningWindow>().ToArray();
            var status = windows.Length == 2
                ? $"Planning available for both allowance windows. {Disclaimer}"
                : $"Planning available for one allowance window. {Disclaimer}";
            return new UsagePlanningResult(
                true,
                status,
                desiredEnd,
                remainingWorkdays,
                primary,
                weekly,
                Disclaimer);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Unavailable(desiredEnd, "Planning paused: the usage window timestamps are out of range.", remainingWorkdays);
        }
        catch (OverflowException)
        {
            return Unavailable(desiredEnd, "Planning paused: the usage window duration is out of range.", remainingWorkdays);
        }
    }

    private static UsagePlanningWindow? CreateWindowPlan(
        RateLimitWindow? window,
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset now,
        DateTimeOffset desiredEnd,
        int? requestedWorkdays,
        TimeSpan maximumSampleAge,
        int adaptiveCycleCount,
        bool isPrimary,
        TimeZoneInfo timeZone)
    {
        if (!UsageFreshness.IsValidWindow(window, now))
        {
            return null;
        }

        var validWindow = window!;
        var horizonEnd = validWindow.ResetsAt < desiredEnd ? validWindow.ResetsAt : desiredEnd;
        var horizon = horizonEnd - now;
        if (horizon <= TimeSpan.Zero)
        {
            return null;
        }

        var workdays = isPrimary
            ? 1
            : Math.Max(1, Math.Min(requestedWorkdays ?? 1, CalendarDaysUntilReset(validWindow.ResetsAt, now, timeZone)));
        var remaining = validWindow.RemainingPercent;
        var pointsPerWorkday = remaining / workdays;
        var pointsPerHour = pointsPerWorkday / horizon.TotalHours;
        var displayLabel = FormatWindowDuration(validWindow.WindowMinutes);
        var resetsBeforeDesiredEnd = validWindow.ResetsAt < desiredEnd;
        var horizonMessage = resetsBeforeDesiredEnd
            ? $"This {displayLabel} window resets before the desired end at {FormatUtc(validWindow.ResetsAt)}; planning stops at that reset."
            : $"Planning runs until {FormatUtc(horizonEnd)}.";

        var forecast = CreateForecast(
            validWindow,
            history,
            now,
            maximumSampleAge);
        var evidence = CreateEvidence(
            validWindow,
            history,
            now,
            maximumSampleAge,
            adaptiveCycleCount);
        var backtest = CreateBacktest(
            validWindow,
            history,
            now,
            maximumSampleAge);

        return new UsagePlanningWindow(
            displayLabel,
            remaining,
            validWindow.WindowMinutes,
            validWindow.ResetsAt,
            horizonEnd,
            workdays,
            horizon,
            pointsPerWorkday,
            pointsPerHour,
            resetsBeforeDesiredEnd,
            horizonMessage,
            forecast,
            evidence,
            backtest);
    }

    private static UsagePlanningForecast CreateForecast(
        RateLimitWindow window,
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset now,
        TimeSpan maximumSampleAge)
    {
        if (!TryGetWindowStart(window, out var windowStart))
        {
            return UsagePlanningForecast.Unavailable("Forecast unavailable: the window duration is out of range.");
        }

        try
        {
            var analysis = UsageTrendAnalyzer.Analyze(
                history ?? Array.Empty<UsageHistoryEntry>(),
                windowStart,
                window.ResetsAt,
                now,
                dataAvailable: true,
                maximumSampleAge,
                adaptiveWeeklyForecastEnabled: false,
                adaptiveWeeklyHistory: null);
            if (analysis.Forecast is null)
            {
                return UsagePlanningForecast.Unavailable(analysis.ForecastStatus);
            }

            return new UsagePlanningForecast(true, analysis.Forecast, analysis.ForecastStatus);
        }
        catch (ArgumentException)
        {
            return UsagePlanningForecast.Unavailable("Forecast unavailable: the measurements are not usable.");
        }
        catch (InvalidOperationException)
        {
            return UsagePlanningForecast.Unavailable("Forecast unavailable: the measurements are not usable.");
        }
        catch (OverflowException)
        {
            return UsagePlanningForecast.Unavailable("Forecast unavailable: the timestamps are out of range.");
        }
    }

    private static UsagePlanningEvidence CreateEvidence(
        RateLimitWindow window,
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset now,
        TimeSpan maximumSampleAge,
        int adaptiveCycleCount)
    {
        var samples = GetWindowSamples(history, window, now);
        var segment = GetLatestSegment(history, window, now, maximumSampleAge);
        var measurementSpan = GetSpan(samples);
        var segmentSpan = GetSpan(segment);
        var summary = $"{samples.Length.ToString(CultureInfo.InvariantCulture)} measurements over {FormatDuration(measurementSpan)}; "
            + $"latest continuous segment: {segment.Length.ToString(CultureInfo.InvariantCulture)} measurements over {FormatDuration(segmentSpan)}; "
            + $"adaptive weekly cycles: {adaptiveCycleCount.ToString(CultureInfo.InvariantCulture)}. "
            + "This is descriptive evidence, not a calibrated reliability probability.";
        return new UsagePlanningEvidence(
            samples.Length,
            measurementSpan,
            segment.Length,
            segmentSpan,
            adaptiveCycleCount,
            summary);
    }

    private static UsagePlanningBacktest CreateBacktest(
        RateLimitWindow window,
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset now,
        TimeSpan maximumSampleAge)
    {
        var segment = GetLatestSegment(history, window, now, maximumSampleAge);
        if (segment.Length < 3)
        {
            return UsagePlanningBacktest.Unavailable(
                "Backtest unavailable: at least three continuous measurements are required to hold out the latest one.");
        }

        var heldOut = segment[^1];
        var training = segment[..^1];
        var first = training[0];
        var last = training[^1];
        var trainingSpan = last.RecordedAt - first.RecordedAt;
        var holdoutInterval = heldOut.RecordedAt - last.RecordedAt;
        if (training.Length < 2
            || trainingSpan <= TimeSpan.Zero
            || holdoutInterval <= TimeSpan.Zero
            || holdoutInterval > MaximumGap(maximumSampleAge))
        {
            return UsagePlanningBacktest.Unavailable(
                "Backtest unavailable: the held-out measurement does not follow a meaningful continuous pace.");
        }

        var consumed = first.RemainingPercent - last.RemainingPercent;
        var rate = consumed / trainingSpan.TotalMinutes;
        if (!double.IsFinite(rate) || rate <= 0)
        {
            return UsagePlanningBacktest.Unavailable(
                "Backtest unavailable: preceding measurements do not show a usable downward pace.");
        }

        var predicted = Math.Clamp(
            last.RemainingPercent - rate * holdoutInterval.TotalMinutes,
            0,
            100);
        var error = Math.Abs(predicted - heldOut.RemainingPercent);
        var status = $"Holdout at {FormatUtc(heldOut.RecordedAt)}: predicted {FormatPercent(predicted)}%, "
            + $"actual {FormatPercent(heldOut.RemainingPercent)}%, absolute error {FormatPercent(error)} percentage points; "
            + "the latest measurement was excluded from the preceding-pace calculation.";
        return new UsagePlanningBacktest(
            true,
            training.Length,
            heldOut.RecordedAt,
            predicted,
            heldOut.RemainingPercent,
            error,
            status);
    }

    private static UsageHistoryEntry[] GetLatestSegment(
        IReadOnlyList<UsageHistoryEntry> history,
        RateLimitWindow window,
        DateTimeOffset now,
        TimeSpan maximumSampleAge)
    {
        if (!TryGetWindowStart(window, out var windowStart))
        {
            return [];
        }

        try
        {
            return UsageTrendHistory.LatestSegment(
                history ?? Array.Empty<UsageHistoryEntry>(),
                windowStart,
                window.ResetsAt,
                now,
                MaximumGap(maximumSampleAge));
        }
        catch (ArgumentException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        catch (OverflowException)
        {
            return [];
        }
    }

    private static UsageHistoryEntry[] GetWindowSamples(
        IReadOnlyList<UsageHistoryEntry> history,
        RateLimitWindow window,
        DateTimeOffset now)
    {
        if (!TryGetWindowStart(window, out var windowStart) || history is null)
        {
            return [];
        }

        try
        {
            return history
                .Where(sample => sample is not null
                    && double.IsFinite(sample.RemainingPercent)
                    && sample.RemainingPercent is >= 0 and <= 100
                    && sample.RecordedAt >= windowStart
                    && sample.RecordedAt <= window.ResetsAt
                    && sample.RecordedAt <= now)
                .OrderBy(sample => sample.RecordedAt)
                .GroupBy(sample => sample.RecordedAt)
                .Select(group => group.Last())
                .ToArray();
        }
        catch (ArgumentException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        catch (OverflowException)
        {
            return [];
        }
    }

    private static TimeSpan? GetSpan(UsageHistoryEntry[] samples)
    {
        if (samples.Length < 2)
        {
            return null;
        }

        try
        {
            return samples[^1].RecordedAt - samples[0].RecordedAt;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int CountAdaptiveCycles(UsagePresentation presentation)
    {
        var history = presentation.AdaptiveWeeklyHistory;
        if (history is null)
        {
            return 0;
        }

        var completed = history.CompletedCycles?.Count(cycle => cycle is not null) ?? 0;
        return completed + (history.ActiveCycle is null ? 0 : 1);
    }

    private static int CalendarDaysUntilReset(DateTimeOffset resetsAt, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var days = (TimeZoneInfo.ConvertTime(resetsAt, timeZone).Date - TimeZoneInfo.ConvertTime(now, timeZone).Date).Days;
        if (days <= 1)
        {
            return 1;
        }

        return days;
    }

    private static TimeSpan MaximumGap(TimeSpan maximumSampleAge)
    {
        if (maximumSampleAge <= TimeSpan.Zero)
        {
            return TimeSpan.FromMinutes(15);
        }

        return maximumSampleAge.Ticks > TimeSpan.MaxValue.Ticks / 3
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(maximumSampleAge.Ticks * 3);
    }

    private static bool TryGetWindowStart(RateLimitWindow window, out DateTimeOffset start)
    {
        try
        {
            start = window.ResetsAt - TimeSpan.FromMinutes(window.WindowMinutes);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            start = default;
            return false;
        }
        catch (OverflowException)
        {
            start = default;
            return false;
        }
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } value)
        {
            return "unknown span";
        }

        if (value.TotalMinutes < 1)
        {
            return $"{Math.Max(1, (int)Math.Round(value.TotalSeconds))} seconds";
        }

        if (value.TotalHours < 1)
        {
            return $"{Math.Round(value.TotalMinutes, 1).ToString("0.#", CultureInfo.InvariantCulture)} minutes";
        }

        if (value.TotalDays < 1)
        {
            return $"{Math.Round(value.TotalHours, 1).ToString("0.#", CultureInfo.InvariantCulture)} hours";
        }

        return $"{Math.Round(value.TotalDays, 1).ToString("0.#", CultureInfo.InvariantCulture)} days";
    }

    private static string FormatPercent(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

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

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static UsagePlanningResult Unavailable(
        DateTimeOffset desiredEnd,
        string status,
        int? remainingWorkdays) =>
        new(false, status, desiredEnd, remainingWorkdays, null, null, Disclaimer);
}

internal sealed record UsagePlanningResult(
    bool IsAvailable,
    string Status,
    DateTimeOffset DesiredEnd,
    int? RequestedWorkdays,
    UsagePlanningWindow? Primary,
    UsagePlanningWindow? Weekly,
    string Disclaimer)
{
    internal UsagePlanningWindow? FiveHour => Primary;

    internal IReadOnlyList<UsagePlanningWindow> Windows =>
        new[] { Primary, Weekly }.Where(window => window is not null).Cast<UsagePlanningWindow>().ToArray();

    internal IReadOnlyList<UsagePlanningEvidence> Evidence =>
        Windows.Select(window => window.Evidence).ToArray();

    internal IReadOnlyList<UsagePlanningBacktest> Backtests =>
        Windows.Select(window => window.Backtest).ToArray();
}

internal sealed record UsagePlanningWindow(
    string Label,
    double RemainingPercent,
    int WindowMinutes,
    DateTimeOffset ResetsAt,
    DateTimeOffset HorizonEnd,
    int Workdays,
    TimeSpan Horizon,
    double AvailablePointsPerWorkday,
    double AvailablePointsPerHour,
    bool ResetsBeforeDesiredEnd,
    string HorizonMessage,
    UsagePlanningForecast Forecast,
    UsagePlanningEvidence Evidence,
    UsagePlanningBacktest Backtest)
{
    internal double Remaining => RemainingPercent;
    internal double QuotaPointsPerWorkday => AvailablePointsPerWorkday;
    internal double QuotaPointsPerHour => AvailablePointsPerHour;
}

internal sealed record UsagePlanningForecast(
    bool IsAvailable,
    UsageTrendForecast? Projection,
    string Status)
{
    internal UsageTrendForecast? Forecast => Projection;
    internal DateTimeOffset? EndsAt => Projection?.EndsAt;
    internal bool ReachesLimitBeforeReset => Projection?.ReachesLimitBeforeReset ?? false;

    internal static UsagePlanningForecast Unavailable(string status) => new(false, null, status);
}

internal sealed record UsagePlanningEvidence(
    int MeasurementCount,
    TimeSpan? MeasurementSpan,
    int SegmentMeasurementCount,
    TimeSpan? SegmentSpan,
    int AdaptiveCycleCount,
    string Summary)
{
    internal string Description => Summary;
}

internal sealed record UsagePlanningBacktest(
    bool IsAvailable,
    int TrainingSampleCount,
    DateTimeOffset? HeldOutAt,
    double? PredictedRemainingPercent,
    double? ActualRemainingPercent,
    double? AbsoluteErrorPercentagePoints,
    string Status)
{
    internal string Message => Status;
    internal static UsagePlanningBacktest Unavailable(string status) => new(false, 0, null, null, null, null, status);
}
