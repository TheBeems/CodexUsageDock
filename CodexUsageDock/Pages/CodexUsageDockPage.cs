using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class CodexUsageDockPage : ContentPage, IDisposable
{
    private static readonly string Version = CodexUsageDockMetadata.Version;
    private readonly CodexUsageService _usage;
    private readonly CodexUsageDockSettingsPage _settings;
    private readonly FormContent _mainContent = new()
    {
        TemplateJson = UsageDashboardCard.TemplateJson,
    };
    private readonly Details _details = new()
    {
        Title = "Usage details",
        Size = ContentSize.Medium,
    };

    public CodexUsageDockPage(CodexUsageService usage, CodexUsageDockSettingsPage settings)
    {
        _usage = usage;
        _settings = settings;
        _usage.Updated += OnUpdated;
        Id = "nl.mathijs.codexusage.details";
        Title = $"Codex Usage - {Version}";
        Name = "Open";
        Details = _details;
        Commands =
        [
            new CommandContextItem(new RefreshUsageCommand(_usage))
            {
                Title = "Refresh now",
            },
            new CommandContextItem(settings)
            {
                Title = settings.Title,
                Icon = settings.Icon,
            },
        ];
        UpdatePresentation();
    }

    public override IContent[] GetContent() => [_mainContent];

    internal static string FormatRefreshStatus(bool isLoading) => isLoading
        ? "> **Refreshing Codex usage…**"
        : string.Empty;

    internal static string FormatMainDataJson(
        CodexUsageSnapshot snapshot,
        DateTimeOffset now,
        bool isLoading,
        IReadOnlyList<UsageHistoryEntry> primaryHistory,
        IReadOnlyList<UsageHistoryEntry> weeklyHistory,
        TimeSpan refreshInterval,
        bool adaptiveWeeklyForecastEnabled = true,
        AdaptiveWeeklyUsageHistory? adaptiveWeeklyHistory = null)
    {
        var dataAvailable = snapshot.Source != UsageDataSource.Unavailable;
        var maximumSampleAge = TrendFreshness(refreshInterval);
        var (statusTitle, statusDescription) = FormatSummaryParts(snapshot, now);
        var data = new JsonObject
        {
            ["isLoading"] = isLoading,
            ["statusTitle"] = statusTitle,
            ["statusDescription"] = statusDescription,
        };

        _ = AddWindowData(
            data,
            "fiveHour",
            "5-hour window",
            snapshot.Primary,
            primaryHistory,
            now,
            dataAvailable,
            maximumSampleAge,
            UsageBarPalette.FiveHour,
            snapshot.Secondary is not null ? "Currently inactive" : "Not available");
        var weeklyTrend = AddWindowData(
            data,
            "weekly",
            "Weekly window",
            snapshot.Secondary,
            weeklyHistory,
            now,
            dataAvailable,
            maximumSampleAge,
            UsageBarPalette.Weekly,
            "Not available",
            adaptiveWeeklyForecastEnabled ? adaptiveWeeklyHistory : null,
            adaptiveWeeklyForecastEnabled);
        AddWeeklyTrendData(
            data,
            snapshot.Secondary,
            weeklyHistory,
            weeklyTrend,
            dataAvailable,
            now,
            TrendMaximumGap(refreshInterval));

        return data.ToJsonString();
    }

    internal static string FormatDetailsBody(
        CodexUsageSnapshot snapshot,
        DateTimeOffset now,
        IReadOnlyList<UsageHistoryEntry>? weeklyHistory = null) => $"""
        ## Resets and credits

        {FormatResetSummary(snapshot.ResetCredits, now)}
        {FormatResetCredits(snapshot.ResetCredits)}
        - **Credits:** {FormatCredits(snapshot.Credits)}

        {FormatRestorationDetails(snapshot.Secondary, weeklyHistory ?? [], now)}

        ## Account and data

        - **Plan:** {FormatPlan(snapshot.PlanType)}
        - **Status:** {FormatDataStatus(snapshot, now)}
        - **Source:** {snapshot.SourceDisplayName}
        {FormatError(snapshot)}
        """;

    internal static string FormatSummary(CodexUsageSnapshot snapshot, DateTimeOffset now)
    {
        var (title, description) = FormatSummaryParts(snapshot, now);
        return $"> **{title}**  \n> {description}";
    }

    private static (string Title, string Description) FormatSummaryParts(CodexUsageSnapshot snapshot, DateTimeOffset now)
    {
        var activeWindow = snapshot.Primary ?? snapshot.Secondary;
        if (activeWindow is null)
        {
            return ("Status: Usage allowance unknown", FormatDataStatus(snapshot, now));
        }

        var remaining = activeWindow.RemainingPercent;
        var title = remaining switch
        {
            <= 10 => "Almost at your limit",
            <= 30 => "Limited allowance available",
            _ => "Plenty of allowance available",
        };
        var weekly = snapshot.Primary is not null && snapshot.Secondary is not null
            ? $" Weekly allowance: {snapshot.Secondary.RemainingPercent:0}%."
            : string.Empty;
        return ($"Status: {title}", $"{remaining:0}% available; resets {FormatRelativeTime(activeWindow.ResetsAt, now)}.{weekly}");
    }

    internal static string FormatCredits(CreditBalance? credits)
    {
        if (credits is null)
        {
            return "not available";
        }

        if (credits.Unlimited)
        {
            return "Unlimited";
        }

        var balance = UsageText.SanitizeExternal(credits.Balance, 64);
        return credits.HasCredits && balance is not null
            ? UsageText.EscapeMarkdown(balance)
            : "no available balance";
    }

    internal static string FormatResetCredits(RateLimitResetCredits? resets)
    {
        if (resets?.Credits is not { Count: > 0 } credits)
        {
            return string.Empty;
        }

        var lines = credits.Select((credit, index) =>
        {
            var title = UsageText.SanitizeExternal(credit.Title) is { } externalTitle
                ? UsageText.EscapeMarkdown(externalTitle)
                : $"Reset {index + 1}";
            var expiry = credit.ExpiresAt is null
                ? "expiration unknown"
                : $"expires {FormatLocalTime(credit.ExpiresAt.Value, "ddd d MMM HH:mm")}";
            return $"  - **{title}:** {expiry}";
        }).ToList();

        if (resets.AvailableCount > credits.Count)
        {
            lines.Add($"  - No expiration details are available for {resets.AvailableCount - credits.Count} reset(s).");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatWindow(string name, RateLimitWindow? window, DateTimeOffset now, string? inactiveMessage = null)
    {
        if (window is null)
        {
            return $"## {name}\n\n**{inactiveMessage ?? "Not available"}**";
        }

        return $"## {name}\n\n**{window.RemainingPercent:0}% available**  \nResets {FormatRelativeTime(window.ResetsAt, now)} · {FormatLocalTime(window.ResetsAt, "ddd d MMM HH:mm")}";
    }

    private static TrendAnalysis? AddWindowData(
        JsonObject data,
        string prefix,
        string windowName,
        RateLimitWindow? window,
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset now,
        bool dataAvailable,
        TimeSpan maximumSampleAge,
        UsageBarPalette palette,
        string inactiveMessage,
        AdaptiveWeeklyUsageHistory? adaptiveWeeklyHistory = null,
        bool adaptiveWeeklyForecastEnabled = false)
    {
        data[$"{prefix}Available"] = window is not null;
        if (window is null)
        {
            data[$"{prefix}State"] = inactiveMessage;
            return null;
        }

        data[$"{prefix}Remaining"] = $"{window.RemainingPercent:0}%";
        data[$"{prefix}Reset"] = $"{FormatRelativeTime(window.ResetsAt, now)}\n{FormatLocalTime(window.ResetsAt, "ddd d MMM HH:mm")}";

        var usedPercent = Math.Clamp(window.UsedPercent, 0, 100);
        var windowStartsAt = window.ResetsAt - TimeSpan.FromMinutes(window.WindowMinutes);
        var elapsedPercent = window.WindowMinutes > 0
            ? Math.Clamp((now - windowStartsAt).TotalMinutes / window.WindowMinutes * 100, 0, 100)
            : 0;
        var (paceStatus, paceColor) = FormatPaceStatus(usedPercent, elapsedPercent);
        data[$"{prefix}UsedPercent"] = $"{usedPercent:0}%";
        data[$"{prefix}ElapsedPercent"] = $"{elapsedPercent:0}%";
        data[$"{prefix}UsedBarUrl"] = UsageDashboardCard.CreateProgressBarImageUrl(usedPercent, palette);
        data[$"{prefix}UsedBarAlt"] = $"{windowName}: {usedPercent:0}% of allowance used.";
        data[$"{prefix}ElapsedBarUrl"] = UsageDashboardCard.CreateProgressBarImageUrl(elapsedPercent, UsageBarPalette.Time);
        data[$"{prefix}ElapsedBarAlt"] = $"{windowName}: {elapsedPercent:0}% of the window elapsed.";
        data[$"{prefix}PaceStatus"] = paceStatus;
        data[$"{prefix}PaceColor"] = paceColor;

        var currentHistory = GetCurrentTrendHistory(history, windowStartsAt);
        var trend = AnalyzeTrend(
            currentHistory,
            windowStartsAt,
            window.ResetsAt,
            now,
            dataAvailable,
            maximumSampleAge,
            adaptiveWeeklyForecastEnabled,
            adaptiveWeeklyHistory);
        data[$"{prefix}Projection"] = trend.Message;
        return trend;
    }

    private static void AddWeeklyTrendData(
        JsonObject data,
        RateLimitWindow? window,
        IReadOnlyList<UsageHistoryEntry> history,
        TrendAnalysis? trend,
        bool dataAvailable,
        DateTimeOffset now,
        TimeSpan maximumGap)
    {
        data["weeklyTrendAvailable"] = false;
        data["weeklyRestorationAvailable"] = false;
        if (window is null || trend is null || !dataAvailable)
        {
            return;
        }

        data["weeklyForecastStatus"] = trend.ForecastStatus;

        var windowStartsAt = window.ResetsAt - TimeSpan.FromMinutes(window.WindowMinutes);
        var chartHistory = GetHistoryInWindow(history, windowStartsAt);
        if (chartHistory.Length < 2)
        {
            return;
        }

        var chart = WeeklyUsageTrendChartRenderer.Create(
            history,
            window,
            now,
            maximumGap,
            trend.Forecast);
        if (chart is null)
        {
            return;
        }

        data["weeklyTrendAvailable"] = true;
        data["weeklyTrendChartUrl"] = chart.ImageUrl;
        data["weeklyTrendChartAlt"] = chart.AltText;

        var restorations = WeeklyAllowanceRestoration.Detect(history, window, now);
        if (restorations.Length > 0)
        {
            data["weeklyRestorationAvailable"] = true;
            data["weeklyRestorationSummary"] = FormatRestorationSummary(restorations);
            if (trend.Forecast is not null)
            {
                data["weeklyProjection"] = $"{trend.Message} Forecast restarted from the latest restoration.";
            }
        }
    }

    private static string FormatRestorationSummary(AllowanceRestoration[] restorations)
    {
        var latest = restorations[^1];
        var count = restorations.Length > 1
            ? $" {restorations.Length.ToString(CultureInfo.CurrentCulture)} detected in this window."
            : string.Empty;
        return $"Latest restoration detected: {FormatLocalTime(latest.DetectedAt, "ddd d MMM HH:mm")} · {latest.PreviousRemainingPercent.ToString("0", CultureInfo.CurrentCulture)}% → {latest.CurrentRemainingPercent.ToString("0", CultureInfo.CurrentCulture)}% (+{latest.IncreasePercent.ToString("0.#", CultureInfo.CurrentCulture)} pp).{count}";
    }

    private static string FormatRestorationDetails(
        RateLimitWindow? window,
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset now)
    {
        if (window is null)
        {
            return string.Empty;
        }

        var restorations = WeeklyAllowanceRestoration.Detect(history, window, now);
        if (restorations.Length == 0)
        {
            return string.Empty;
        }

        var rows = restorations
            .Reverse()
            .Select(restoration => $"- **{FormatLocalTime(restoration.DetectedAt, "ddd d MMM HH:mm")}:** {restoration.PreviousRemainingPercent.ToString("0", CultureInfo.CurrentCulture)}% → {restoration.CurrentRemainingPercent.ToString("0", CultureInfo.CurrentCulture)}% (+{restoration.IncreasePercent.ToString("0.#", CultureInfo.CurrentCulture)} pp)");
        return $"## Detected allowance restorations\n\n{string.Join(Environment.NewLine, rows)}";
    }

    private static (string Status, string Color) FormatPaceStatus(double usedPercent, double elapsedPercent)
    {
        if (usedPercent <= 1)
        {
            return ("No significant usage yet", "Good");
        }

        if (elapsedPercent < 1 && usedPercent <= 5)
        {
            return ("Window has just started", "Default");
        }

        var paceDifference = usedPercent - elapsedPercent;
        if (paceDifference <= -10)
        {
            return ("Comfortably on track", "Good");
        }

        if (paceDifference <= 5)
        {
            return ("On track", "Good");
        }

        if (paceDifference <= 20)
        {
            return ("Usage is running ahead of time", "Warning");
        }

        return ("Limit may be reached before reset", "Attention");
    }

    internal static string FormatRelativeTime(DateTimeOffset target, DateTimeOffset now)
    {
        var duration = target - now;
        if (duration <= TimeSpan.Zero)
        {
            return "now";
        }

        if (duration < TimeSpan.FromMinutes(1)) return "in less than a minute";
        if (duration < TimeSpan.FromHours(1)) return $"in {(int)Math.Ceiling(duration.TotalMinutes)} minutes";
        if (duration < TimeSpan.FromHours(24))
        {
            var hours = (int)duration.TotalHours;
            var minutes = duration.Minutes;
            return minutes == 0 ? $"in {hours} hours" : $"in {hours} hours and {minutes} minutes";
        }

        var local = target.ToLocalTime();
        if (local.Date == now.ToLocalTime().Date.AddDays(1)) return $"tomorrow at {local.ToString("HH:mm", CultureInfo.CurrentCulture)}";
        return $"in {(int)Math.Ceiling(duration.TotalDays)} days";
    }

    internal static string FormatTrend(IReadOnlyList<UsageHistoryEntry> history, DateTimeOffset now, bool dataAvailable = true) =>
        $"## Usage trend\n\n{FormatTrendBodyForReset(history, null, null, now, dataAvailable, TimeSpan.FromMinutes(5))}";

    internal static string FormatTrend(
        string title,
        IReadOnlyList<UsageHistoryEntry> history,
        RateLimitWindow? window,
        DateTimeOffset now,
        bool dataAvailable,
        TimeSpan maximumSampleAge,
        bool adaptiveWeeklyForecastEnabled = false,
        AdaptiveWeeklyUsageHistory? adaptiveWeeklyHistory = null)
    {
        if (window is null)
        {
            return string.Empty;
        }

        return $"## {title}\n\n{FormatTrendBody(history, window, now, dataAvailable, maximumSampleAge, adaptiveWeeklyForecastEnabled, adaptiveWeeklyHistory)}";
    }

    private static string FormatTrendBody(
        IReadOnlyList<UsageHistoryEntry> history,
        RateLimitWindow window,
        DateTimeOffset now,
        bool dataAvailable,
        TimeSpan maximumSampleAge,
        bool adaptiveWeeklyForecastEnabled = false,
        AdaptiveWeeklyUsageHistory? adaptiveWeeklyHistory = null)
    {
        var windowStartsAt = window.ResetsAt - TimeSpan.FromMinutes(window.WindowMinutes);
        return FormatTrendBodyForReset(
            history,
            windowStartsAt,
            window.ResetsAt,
            now,
            dataAvailable,
            maximumSampleAge,
            adaptiveWeeklyForecastEnabled,
            adaptiveWeeklyHistory);
    }

    private static string FormatTrendBodyForReset(
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset? windowStartsAt,
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        bool dataAvailable,
        TimeSpan maximumSampleAge,
        bool adaptiveWeeklyForecastEnabled = false,
        AdaptiveWeeklyUsageHistory? adaptiveWeeklyHistory = null)
    {
        var analysis = AnalyzeTrend(
            history,
            windowStartsAt,
            resetsAt,
            now,
            dataAvailable,
            maximumSampleAge,
            adaptiveWeeklyForecastEnabled,
            adaptiveWeeklyHistory);
        if (analysis.HistoryValues is null)
        {
            return analysis.Message;
        }

        var message = analysis.IsEstimate ? $"*{analysis.Message}*" : analysis.Message;
        return $"{analysis.HistoryValues}  \n{message}";
    }

    private static TrendAnalysis AnalyzeTrend(
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

        var currentWindow = GetCurrentTrendHistory(history, windowStartsAt);
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

        if (now - last.RecordedAt > maximumSampleAge)
        {
            return new(currentWindow, values, "Projection paused because the latest measurement is too old.", false, null, "Forecast: waiting for a fresh measurement.");
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

    private sealed record TrendAnalysis(
        UsageHistoryEntry[] History,
        string? HistoryValues,
        string Message,
        bool IsEstimate,
        UsageTrendForecast? Forecast,
        string ForecastStatus);

    private static UsageHistoryEntry[] GetCurrentTrendHistory(
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset? windowStartsAt)
    {
        var historyInCurrentWindow = GetHistoryInWindow(history, windowStartsAt);
        var segmentStart = 0;
        for (var index = 1; index < historyInCurrentWindow.Length; index++)
        {
            if (WeeklyAllowanceRestoration.IsIncrease(historyInCurrentWindow[index - 1], historyInCurrentWindow[index]))
            {
                segmentStart = index;
            }
        }

        return historyInCurrentWindow[segmentStart..];
    }

    private static UsageHistoryEntry[] GetHistoryInWindow(
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset? windowStartsAt) =>
        windowStartsAt is { } start
            ? history.Where(sample => sample.RecordedAt >= start).ToArray()
            : history.ToArray();

    private static string FormatLimitEstimate(DateTimeOffset estimated, DateTimeOffset now) =>
        estimated.ToLocalTime().Date == now.ToLocalTime().Date
            ? FormatLocalTime(estimated, "HH:mm")
            : FormatLocalTime(estimated, "ddd d MMM HH:mm");

    private static TimeSpan TrendFreshness(TimeSpan refreshInterval) =>
        refreshInterval > TimeSpan.FromMinutes(5) ? refreshInterval : TimeSpan.FromMinutes(5);

    private static TimeSpan TrendMaximumGap(TimeSpan refreshInterval) =>
        TimeSpan.FromTicks(TrendFreshness(refreshInterval).Ticks * 3);

    internal static string FormatResetSummary(RateLimitResetCredits? resets, DateTimeOffset now)
    {
        if (resets is null) return "- **Resets:** not available";
        var nextExpiry = resets.Credits?.Where(credit => credit.ExpiresAt > now).MinBy(credit => credit.ExpiresAt)?.ExpiresAt;
        var expiry = nextExpiry is null ? string.Empty : $" · next one expires {FormatRelativeTime(nextExpiry.Value, now)}";
        return $"- **Resets:** {resets.AvailableCount.ToString(CultureInfo.CurrentCulture)} available{expiry}";
    }

    internal static string FormatDataStatus(CodexUsageSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Source == UsageDataSource.Unavailable)
        {
            return $"Data not available · last attempt at {FormatLocalTime(snapshot.UpdatedAt, "HH:mm")}";
        }

        var age = now - snapshot.UpdatedAt;
        var freshness = age < TimeSpan.FromMinutes(2)
            ? $"just updated at {FormatLocalTime(snapshot.UpdatedAt, "HH:mm")}"
            : $"possibly outdated · updated {FormatRelativeAge(age)} ago";
        var mode = snapshot.Source switch
        {
            UsageDataSource.AppServer => "Live",
            UsageDataSource.LocalSession => "Local fallback data",
            UsageDataSource.Initializing => "Loading data",
            UsageDataSource.Unavailable => "Data not available",
            _ => "Data not available",
        };
        return $"{mode} · {freshness}";
    }

    internal static string FormatPlan(string? plan)
    {
        var safePlan = UsageText.SanitizeExternal(plan, 32);
        return safePlan is null
            ? "Unknown"
            : UsageText.EscapeMarkdown(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(safePlan.Replace('_', ' ')));
    }

    private static string FormatRelativeAge(TimeSpan age) => age < TimeSpan.FromHours(1)
        ? $"{Math.Max(1, (int)age.TotalMinutes)} minutes"
        : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} hours" : $"{(int)age.TotalDays} days";

    internal static string FormatError(CodexUsageSnapshot snapshot)
    {
        if (snapshot.Source == UsageDataSource.LocalSession && snapshot.Error is not null)
        {
            return $"> **Live Codex data is unavailable. Showing local fallback data.**  \n> {CodexUsageService.LiveDataUnavailableMessage}";
        }

        return snapshot.Source == UsageDataSource.Unavailable
            ? $"> **Codex usage data is unavailable.**  \n> {CodexUsageService.AllDataUnavailableMessage}"
            : string.Empty;
    }

    private static string FormatLocalTime(DateTimeOffset value, string format) =>
        value.ToLocalTime().ToString(format, CultureInfo.CurrentCulture);

    private void UpdatePresentation()
    {
        var snapshot = _usage.Current;
        var now = DateTimeOffset.Now;
        IsLoading = _usage.IsLoading;
        _mainContent.DataJson = FormatMainDataJson(
            snapshot,
            now,
            _usage.IsLoading,
            _usage.PrimaryHistory,
            _usage.WeeklyHistory,
            _usage.RefreshInterval,
            _settings.UseAdaptiveWeeklyForecast,
            _usage.AdaptiveWeeklyHistory);
        _details.Body = FormatDetailsBody(snapshot, now, _usage.WeeklyHistory);
        RaiseItemsChanged(0);
    }

    private void OnUpdated(object? sender, EventArgs e) => UpdatePresentation();

    public void Dispose()
    {
        _usage.Updated -= OnUpdated;
        GC.SuppressFinalize(this);
    }
}
