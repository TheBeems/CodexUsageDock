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

    private static string FormatLimitEstimate(DateTimeOffset estimated, DateTimeOffset now) =>
        estimated.ToLocalTime().Date == now.ToLocalTime().Date
            ? estimated.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)
            : estimated.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture);

}
