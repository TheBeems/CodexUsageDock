using System.Globalization;

namespace CodexUsageDock;

internal static class UsageTrendAnalyzer
{
    internal static TrendAnalysis Analyze(
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset? windowStartsAt,
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        bool dataAvailable,
        TimeSpan maximumSampleAge,
        bool adaptiveWeeklyForecastEnabled = false,
        AdaptiveWeeklyUsageHistory? adaptiveWeeklyHistory = null)
    {
        if (!dataAvailable)
        {
            return new([], null, "Projection unavailable until fresh usage data is loaded.", false, null, "Forecast unavailable.");
        }

        var currentWindow = UsageTrendHistory.LatestSegment(history, windowStartsAt, resetsAt, now, TimeSpan.FromTicks(maximumSampleAge.Ticks * 3));
        if (currentWindow.Length > 0 && now - currentWindow[^1].RecordedAt > maximumSampleAge)
        {
            return new(currentWindow, null, "Projection paused because the latest measurement is too old.", false, null, "Forecast: waiting for a fresh measurement.");
        }

        if (currentWindow.Length < 2)
        {
            return new(currentWindow, null, "Projection will appear after another measurement.", false, null, "Forecast: waiting for another measurement.");
        }

        var samples = currentWindow.Length <= 5 ? currentWindow : currentWindow.Where((_, index) => index % Math.Max(1, currentWindow.Length / 4) == 0).Take(4).Append(currentWindow[^1]).ToArray();
        var values = string.Join(" → ", samples.Select(sample => $"{sample.RemainingPercent:0}%"));
        var first = currentWindow[0];
        var last = currentWindow[^1];
        var isWeekly = resetsAt - windowStartsAt == TimeSpan.FromDays(7);
        if (isWeekly)
        {
            return AnalyzeWeekly(currentWindow, values, windowStartsAt!.Value, resetsAt!.Value, now,
                adaptiveWeeklyForecastEnabled, adaptiveWeeklyHistory);
        }

        var elapsedMinutes = (last.RecordedAt - first.RecordedAt).TotalMinutes;
        var consumed = first.RemainingPercent - last.RemainingPercent;
        if (elapsedMinutes < 2 || consumed <= 0.5)
        {
            return new(currentWindow, values, "No meaningful change yet; projection pending.", false, null, "Forecast: waiting for a meaningful change.");
        }

        var currentRate = consumed / elapsedMinutes;
        if (resetsAt is { } reset)
        {
            AdaptiveWeeklyForecastProjection projection;
            if (windowStartsAt is { } start)
            {
                projection = AdaptiveWeeklyForecast.Project(
                    last,
                    start,
                    reset,
                    currentRate,
                    adaptiveWeeklyForecastEnabled,
                    adaptiveWeeklyHistory);
            }
            else
            {
                var estimatedAtCurrentRate = last.RecordedAt.AddMinutes(last.RemainingPercent / currentRate);
                var forecast = estimatedAtCurrentRate >= reset
                    ? new UsageTrendForecast(
                        reset,
                        Math.Max(0, last.RemainingPercent - currentRate * (reset - last.RecordedAt).TotalMinutes),
                        false,
                        [new UsageTrendForecastPoint(reset, Math.Max(0, last.RemainingPercent - currentRate * (reset - last.RecordedAt).TotalMinutes))])
                    : new UsageTrendForecast(
                        estimatedAtCurrentRate,
                        0,
                        true,
                        [new UsageTrendForecastPoint(estimatedAtCurrentRate, 0)]);
                projection = new AdaptiveWeeklyForecastProjection(forecast, "Forecast: current pace only.");
            }

            if (!projection.Forecast.ReachesLimitBeforeReset)
            {
                return new(
                    currentWindow,
                    values,
                    $"Projected at reset: {projection.Forecast.RemainingPercent:0}% available.",
                    true,
                    projection.Forecast,
                    projection.Status);
            }

            return new(
                currentWindow,
                values,
                $"At the current rate, the limit may be reached around {FormatLimitEstimate(projection.Forecast.EndsAt, now)}.",
                true,
                projection.Forecast,
                projection.Status);
        }

        var minutesToEmpty = last.RemainingPercent / currentRate;
        var estimated = last.RecordedAt.AddMinutes(minutesToEmpty);

        return new(
            currentWindow,
            values,
            $"At the current rate, the limit may be reached around {FormatLimitEstimate(estimated, now)}.",
            true,
            new UsageTrendForecast(estimated, 0, true, [new UsageTrendForecastPoint(estimated, 0)]),
            "Forecast: current pace only.");
    }

    internal sealed record TrendAnalysis(
        UsageHistoryEntry[] History,
        string? HistoryValues,
        string Message,
        bool IsEstimate,
        UsageTrendForecast? Forecast,
        string ForecastStatus);

    private static TrendAnalysis AnalyzeWeekly(
        UsageHistoryEntry[] segment, string values, DateTimeOffset start, DateTimeOffset reset, DateTimeOffset now,
        bool adaptiveEnabled, AdaptiveWeeklyUsageHistory? history)
    {
        var last = segment[^1];
        var cutoff = last.RecordedAt.AddHours(-6);
        var first = segment[0];
        for (var index = 1; index < segment.Length && segment[index - 1].RecordedAt < cutoff; index++)
        {
            var previous = segment[index - 1];
            var next = segment[index];
            if (next.RecordedAt < cutoff)
            {
                continue;
            }

            var fraction = (cutoff - previous.RecordedAt).TotalMinutes / (next.RecordedAt - previous.RecordedAt).TotalMinutes;
            first = new(cutoff, previous.RemainingPercent + fraction * (next.RemainingPercent - previous.RemainingPercent));
            break;
        }

        var duration = last.RecordedAt - first.RecordedAt;
        if (duration < TimeSpan.FromMinutes(30))
        {
            return new(segment, values, "Weekly projection needs at least 30 minutes of continuous measurements.", false, null,
                $"Forecast: collecting measurements ({duration.TotalMinutes:0}/30 minutes).");
        }

        var consumed = first.RemainingPercent - last.RemainingPercent;
        var projection = AdaptiveWeeklyForecast.Project(last, start, reset, consumed / duration.TotalMinutes,
            adaptiveEnabled, history, duration);
        if (consumed <= 0.5 && !projection.UsesHistory)
        {
            return new(segment, values, "No meaningful recent change; weekly projection pending.", false, null, projection.Status);
        }

        var condition = projection.UsesHistory ? "With recent usage and observed history" : "If this recent pace continues";
        var forecast = projection.Forecast;
        var message = forecast.ReachesLimitBeforeReset
            ? $"{condition}, the limit may be reached around {FormatWeeklyLimitEstimate(forecast.EndsAt, now)}."
            : $"Projected at reset: about {forecast.RemainingPercent:0}% available. {condition}.";
        return new(segment, values, message, true, forecast, projection.Status);
    }

    internal static string FormatWeeklyLimitEstimate(DateTimeOffset estimated, DateTimeOffset now,
        CultureInfo? culture = null, TimeZoneInfo? timeZone = null)
    {
        var displayCulture = culture ?? CultureInfo.InvariantCulture;
        var displayTimeZone = timeZone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(estimated, displayTimeZone);
        if (estimated - now >= TimeSpan.FromDays(1))
        {
            return local.ToString("ddd d MMM", displayCulture);
        }

        // Round only the presentation; alert thresholds and chart points retain the calculated instant.
        var quarter = TimeSpan.FromMinutes(15).Ticks;
        var rounded = local.AddTicks((quarter - local.Ticks % quarter) % quarter);
        return rounded.Date == TimeZoneInfo.ConvertTime(now, displayTimeZone).Date
            ? rounded.ToString("HH:mm", displayCulture)
            : rounded.ToString("ddd d MMM HH:mm", displayCulture);
    }

    private static string FormatLimitEstimate(DateTimeOffset estimated, DateTimeOffset now) =>
        estimated.ToLocalTime().Date == now.ToLocalTime().Date
            ? estimated.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : estimated.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

}
