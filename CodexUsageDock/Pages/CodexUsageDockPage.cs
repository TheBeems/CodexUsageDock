using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class CodexUsageDockPage : ContentPage, IDisposable
{
    private static readonly string Version = CodexUsageDockMetadata.Version;
    private readonly object _presentationLock = new();
    private bool _disposed;
    private readonly CodexUsageService _usage;
    private readonly CodexUsageDockSettingsPage _settings;
    private readonly UsageDashboardForm _mainContent;
    private bool _showDetails;
    private readonly Details _details = new()
    {
        Title = "Usage details",
        Size = ContentSize.Medium,
    };

    public CodexUsageDockPage(CodexUsageService usage, CodexUsageDockSettingsPage settings)
    {
        _usage = usage;
        _mainContent = new UsageDashboardForm(ToggleDetails) { TemplateJson = UsageDashboardCard.TemplateJson };
        _settings = settings;
        _usage.Updated += OnUpdated;
        Id = "nl.mathijs.codexusage.details";
        Title = $"Codex Usage - {Version}";
        Name = "Open";
        Details = null;
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
        AdaptiveWeeklyUsageHistory? adaptiveWeeklyHistory = null,
        LocalTokenUsageSnapshot? tokenUsage = null,
        bool showDetails = false)
    {
        var freshness = GetFreshnessState(snapshot, now, refreshInterval);
        var dataAvailable = IsDataAvailable(snapshot, freshness);
        var maximumSampleAge = TrendFreshness(refreshInterval);
        var notice = FormatDashboardNotice(snapshot, now, freshness);
        var nextExpiry = snapshot.ResetCredits?.Credits?.Where(credit => credit.ExpiresAt > now)
            .MinBy(credit => credit.ExpiresAt)?.ExpiresAt;
        var urgentReset = nextExpiry is { } expiry && expiry - now <= TimeSpan.FromDays(1);
        var resetCount = snapshot.ResetCredits?.AvailableCount ?? 0;
        var data = new JsonObject
        {
            ["isLoading"] = isLoading,
            ["notice"] = notice,
            ["hasNotice"] = notice.Length > 0,
            ["hasResetCredits"] = resetCount > 0,
            ["resetCreditsSummary"] = resetCount > 0
                ? $"{resetCount} reset credit{(resetCount == 1 ? string.Empty : "s")}{(urgentReset ? $" · expires {FormatRelativeTime(nextExpiry!.Value, now)}" : string.Empty)}"
                : string.Empty,
            ["resetCreditsColor"] = urgentReset ? "Warning" : "Default",
            ["detailsButtonTitle"] = showDetails ? "Hide details" : "Details",
            ["quotaGroups"] = CreateQuotaGroups(snapshot, now, dataAvailable),
            ["weeklyAvailable"] = UsageFreshness.IsValidWindow(snapshot.Secondary, now),
        };

        var primaryTrend = AnalyzeWindow(snapshot.Primary, primaryHistory, now, dataAvailable, maximumSampleAge);
        var weeklyTrend = AnalyzeWindow(snapshot.Secondary, weeklyHistory, now, dataAvailable,
            maximumSampleAge, adaptiveWeeklyForecastEnabled, adaptiveWeeklyHistory);
        data["fiveHourProjection"] = primaryTrend?.Message ?? string.Empty;
        data["weeklyProjection"] = weeklyTrend?.Message ?? string.Empty;
        data["weeklyBudget"] = snapshot.Secondary is { } weekly
            ? FormatWeeklyBudget(weekly, now, dataAvailable) : string.Empty;
        data["weeklyForecastSummary"] = FormatForecastSummary(weeklyTrend, now, dataAvailable, maximumSampleAge);
        AddWeeklyTrendData(
            data, snapshot.Secondary, weeklyHistory, weeklyTrend, dataAvailable,
            snapshot.Source is UsageDataSource.AppServer or UsageDataSource.LocalSession or UsageDataSource.LastConfirmed,
            now, TrendMaximumGap(refreshInterval), showDetails ? tokenUsage : null);
        return data.ToJsonString();
    }

    private static string FormatDashboardNotice(CodexUsageSnapshot snapshot, DateTimeOffset now, UsageFreshnessState freshness)
    {
        var status = snapshot.Source == UsageDataSource.Initializing ? string.Empty : freshness switch
        {
            UsageFreshnessState.LastConfirmed => $"Last confirmed · {FormatAge(snapshot.UpdatedAt, now)} ago",
            UsageFreshnessState.Stale => $"Outdated · {FormatAge(snapshot.UpdatedAt, now)} ago",
            UsageFreshnessState.Future => "Timestamp needs confirmation",
            UsageFreshnessState.Unknown => "Usage unavailable",
            _ => snapshot.Source == UsageDataSource.LocalSession ? "Local fallback data" : string.Empty,
        };
        return snapshot.OrdinaryUsageAllowed == false
            ? $"Usage blocked{(status.Length > 0 ? $" · {status}" : string.Empty)}"
            : status;
    }

    private static JsonArray CreateQuotaGroups(CodexUsageSnapshot snapshot, DateTimeOffset now, bool fresh)
    {
        var groups = new JsonArray();
        var defaultWindows = new List<(string Title, RateLimitWindow? Window)>
        {
            ("5-hour", snapshot.Primary),
            ("Weekly", snapshot.Secondary),
        };
        foreach (var bucket in snapshot.Buckets ?? [])
        {
            if (!string.Equals(bucket.Id, snapshot.DefaultBucketId, StringComparison.Ordinal)) continue;
            foreach (var window in new[] { bucket.Primary, bucket.Secondary })
            {
                if (window is not null && window != snapshot.Primary && window != snapshot.Secondary)
                    defaultWindows.Add((FormatWindowDuration(window.WindowMinutes), window));
            }
        }
        groups.Add((JsonNode)CreateQuotaGroup("Codex", defaultWindows, now, fresh));
        foreach (var bucket in snapshot.Buckets ?? [])
        {
            if (string.Equals(bucket.Id, snapshot.DefaultBucketId, StringComparison.Ordinal)) continue;
            var name = UsageText.SanitizeExternal(bucket.Name) ?? UsageText.SanitizeExternal(bucket.Id) ?? "Additional quota";
            // Additional categories can use arbitrary durations in either slot.
            var windows = new[] { bucket.Primary, bucket.Secondary }
                .Where(window => window is not null)
                .Select(window => ("Quota", window))
                .ToArray();
            groups.Add((JsonNode)CreateQuotaGroup(name,
                windows.Length > 0 ? windows : [("Quota", null)], now, fresh));
        }
        return groups;
    }

    private static JsonObject CreateQuotaGroup(
        string name, IEnumerable<(string Title, RateLimitWindow? Window)> windows, DateTimeOffset now, bool fresh)
    {
        var active = new JsonArray();
        var inactive = new List<string>();
        foreach (var (fallbackTitle, window) in windows)
        {
            var title = window is { WindowMinutes: > 0 } ? FormatWindowDuration(window.WindowMinutes) : fallbackTitle;
            if (string.Equals(title, "weekly", StringComparison.Ordinal)) title = "Weekly";
            if (!UsageFreshness.IsValidWindow(window, now) || window is null)
            {
                inactive.Add($"{title} · {(window is null ? "Not reported" : "Awaiting refresh")}");
                continue;
            }

            var startsAt = window.ResetsAt - TimeSpan.FromMinutes(window.WindowMinutes);
            var elapsedAvailable = fresh && startsAt <= now;
            var elapsed = Math.Clamp((now - startsAt).TotalMinutes / window.WindowMinutes * 100, 0, 100);
            active.Add((JsonNode)new JsonObject
            {
                ["title"] = title,
                ["usedPercent"] = $"{window.UsedPercent:0}%",
                ["usedColor"] = !fresh ? "Default" : window.UsedPercent >= 90 ? "Attention"
                    : window.UsedPercent >= 70 ? "Warning" : "Default",
                ["usedBarUrl"] = UsageDashboardCard.CreateProgressBarImageUrl(window.UsedPercent, UsageBarPalette.Used),
                ["usedBarAlt"] = $"{name}, {title}: {window.UsedPercent:0}% used{(fresh ? string.Empty : " at last observation")}.",
                ["elapsedAvailable"] = elapsedAvailable,
                ["elapsedPercent"] = elapsedAvailable ? $"{elapsed:0}%" : "—",
                ["elapsedBarUrl"] = elapsedAvailable ? UsageDashboardCard.CreateProgressBarImageUrl(elapsed, UsageBarPalette.Time) : string.Empty,
                ["elapsedBarAlt"] = elapsedAvailable ? $"{name}, {title}: {elapsed:0}% of time elapsed." : "Time comparison unavailable.",
                ["reset"] = $"Resets {window.ResetsAt.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)}",
            });
        }

        foreach (var window in active)
        {
            window!["showTitle"] = active.Count > 1;
        }
        var heading = active.Count == 1 ? $"{name} · {active[0]!["title"]!.GetValue<string>()}" : name;
        return new JsonObject
        {
            ["name"] = UsageText.EscapeMarkdown(name),
            ["heading"] = UsageText.EscapeMarkdown(heading),
            ["singleWindow"] = active.Count == 1,
            ["windows"] = active,
            ["inactiveWindows"] = string.Join(" · ", inactive),
            ["hasInactiveWindows"] = inactive.Count > 0,
        };
    }

    private static UsageTrendAnalyzer.TrendAnalysis? AnalyzeWindow(
        RateLimitWindow? window, IReadOnlyList<UsageHistoryEntry> history, DateTimeOffset now,
        bool fresh, TimeSpan maximumSampleAge, bool adaptive = false, AdaptiveWeeklyUsageHistory? learned = null) =>
        UsageFreshness.IsValidWindow(window, now) && window is not null
            ? UsageTrendAnalyzer.Analyze(history, window.ResetsAt - TimeSpan.FromMinutes(window.WindowMinutes),
                window.ResetsAt, now, fresh, maximumSampleAge, adaptive, adaptive ? learned : null)
            : null;

    private static string FormatForecastSummary(
        UsageTrendAnalyzer.TrendAnalysis? trend, DateTimeOffset now, bool fresh, TimeSpan maximumSampleAge)
    {
        if (!fresh) return "Forecast paused · refresh needed";
        if (trend?.Forecast is { } forecast)
            return forecast.ReachesLimitBeforeReset
                ? $"Estimated limit · {UsageTrendAnalyzer.FormatWeeklyLimitEstimate(forecast.EndsAt, now, CultureInfo.InvariantCulture)}"
                : $"Forecast · {100 - forecast.RemainingPercent:0}% used at reset";
        if (trend?.History is not { Length: > 0 } history) return "Forecast · collecting measurements";
        if (now - history[^1].RecordedAt > maximumSampleAge) return "Forecast paused · fresh measurement needed";
        var minutes = (history[^1].RecordedAt - history[0].RecordedAt).TotalMinutes;
        return minutes < 30
            ? $"Forecast · {Math.Floor(minutes):0}/30 min of continuous data"
            : "Forecast · waiting for a usage change";
    }

    internal static string FormatDetailsBody(
        CodexUsageSnapshot snapshot,
        DateTimeOffset now,
        IReadOnlyList<UsageHistoryEntry>? weeklyHistory = null,
        TimeSpan? refreshInterval = null) => $"""
        ## Resets and credits

        {FormatResetSummary(snapshot.ResetCredits, now)}
        {FormatResetCredits(snapshot.ResetCredits)}
        - **Credits:** {FormatCredits(snapshot.Credits)}

        {FormatRestorationDetails(snapshot.Secondary, weeklyHistory ?? [], now)}

        {FormatAdditionalBucketDetails(snapshot, now)}

        ## Account and data

        - **Plan:** {FormatPlan(snapshot.PlanType)}
        - **Status:** {FormatDataStatus(snapshot, now, refreshInterval)}
        - **Source:** {snapshot.SourceDisplayName}
        {FormatError(snapshot)}
        """;

    internal static string FormatAdditionalBucketDetails(CodexUsageSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Buckets is not { Count: > 0 } buckets)
        {
            return string.Empty;
        }

        var rows = new List<string>();
        foreach (var bucket in buckets)
        {
            var label = UsageText.SanitizeExternal(bucket.Name)
                ?? UsageText.SanitizeExternal(bucket.Id)
                ?? "Additional quota";
            var isDefaultBucket = string.Equals(bucket.Id, snapshot.DefaultBucketId, StringComparison.Ordinal);
            AppendAdditionalBucketWindow(rows, label, "primary", bucket.Primary, snapshot, now, isDefaultBucket);
            AppendAdditionalBucketWindow(rows, label, "secondary", bucket.Secondary, snapshot, now, isDefaultBucket);
        }

        return rows.Count == 0
            ? string.Empty
            : $"## Other quota windows\n\n{string.Join(Environment.NewLine, rows)}";
    }

    private static void AppendAdditionalBucketWindow(
        List<string> rows,
        string label,
        string role,
        RateLimitWindow? window,
        CodexUsageSnapshot snapshot,
        DateTimeOffset now,
        bool skipKnownDefaultWindow)
    {
        if (window is null
            || skipKnownDefaultWindow && (window == snapshot.Primary || window == snapshot.Secondary)
            || !UsageFreshness.IsValidWindow(window, now))
        {
            return;
        }

        var safeLabel = UsageText.EscapeMarkdown(label);
        var duration = FormatWindowDuration(window.WindowMinutes);
        rows.Add($"- **{safeLabel} ({role}, {duration}):** {window.RemainingPercent:0}% available · resets {FormatRelativeTime(window.ResetsAt, now)}.");
    }

    private static string FormatWindowDuration(int windowMinutes)
    {
        if (windowMinutes == (int)TimeSpan.FromHours(5).TotalMinutes)
        {
            return "5-hour";
        }

        if (windowMinutes == (int)TimeSpan.FromDays(7).TotalMinutes)
        {
            return "weekly";
        }

        if (windowMinutes > 0 && windowMinutes % (int)TimeSpan.FromDays(1).TotalMinutes == 0)
        {
            return $"{windowMinutes / (int)TimeSpan.FromDays(1).TotalMinutes}-day";
        }

        if (windowMinutes > 0 && windowMinutes % 60 == 0)
        {
            return $"{windowMinutes / 60}-hour";
        }

        return $"{windowMinutes}-minute";
    }

    internal static string FormatSummary(
        CodexUsageSnapshot snapshot,
        DateTimeOffset now,
        TimeSpan? refreshInterval = null)
    {
        var (title, description) = FormatSummaryParts(snapshot, now, refreshInterval);
        return $"> **{title}**  \n> {description}";
    }

    private static (string Title, string Description) FormatSummaryParts(
        CodexUsageSnapshot snapshot,
        DateTimeOffset now,
        TimeSpan? refreshInterval = null)
    {
        var primaryWindow = UsageFreshness.IsValidWindow(snapshot.Primary, now) ? snapshot.Primary : null;
        var secondaryWindow = UsageFreshness.IsValidWindow(snapshot.Secondary, now) ? snapshot.Secondary : null;
        var activeWindow = primaryWindow is { } primary && secondaryWindow is { } secondary
            ? (primary.RemainingPercent <= secondary.RemainingPercent ? primary : secondary)
            : primaryWindow ?? secondaryWindow;
        var freshness = GetFreshnessState(snapshot, now, refreshInterval);
        if (freshness == UsageFreshnessState.Fresh && snapshot.OrdinaryUsageAllowed == false)
        {
            return (
                "Status: Ordinary usage blocked",
                "Ordinary usage is currently blocked for this account.");
        }

        if (activeWindow is null)
        {
            var statusTitle = freshness switch
            {
                UsageFreshnessState.LastConfirmed => "Status: Last confirmed usage",
                UsageFreshnessState.Stale => "Status: Usage data is stale",
                UsageFreshnessState.Future => "Status: Usage timestamp is in the future",
                _ => "Status: Usage allowance unknown",
            };
            return (statusTitle, FormatDataStatus(snapshot, now, refreshInterval));
        }

        if (freshness is UsageFreshnessState.LastConfirmed or UsageFreshnessState.Stale)
        {
            var state = freshness == UsageFreshnessState.LastConfirmed ? "Last confirmed usage" : "Usage data is stale";
            var description = $"Last known allowance: {FormatWindowSummary(activeWindow, snapshot, now)}";
            return ($"Status: {state}", description);
        }

        if (freshness == UsageFreshnessState.Future)
        {
            return (
                "Status: Usage timestamp is in the future",
                $"{FormatWindowSummary(activeWindow, snapshot, now)} Timestamp needs confirmation.");
        }

        if (freshness == UsageFreshnessState.Unknown)
        {
            return (
                "Status: Usage freshness unknown",
                $"{FormatWindowSummary(activeWindow, snapshot, now)}");
        }

        var remaining = activeWindow.RemainingPercent;
        var allowanceTitle = remaining switch
        {
            <= 10 => "Almost at your limit",
            <= 30 => "Limited allowance available",
            _ => "Plenty of allowance available",
        };
        var weekly = primaryWindow is not null && secondaryWindow is not null
            ? $" Weekly allowance: {secondaryWindow.RemainingPercent:0}%."
            : string.Empty;
        var limitingWindow = primaryWindow is not null && secondaryWindow is not null
            ? (ReferenceEquals(activeWindow, secondaryWindow) ? "Weekly window: " : "5-hour window: ")
            : string.Empty;
        return ($"Status: {allowanceTitle}", $"{limitingWindow}{remaining:0}% available; resets {FormatRelativeTime(activeWindow.ResetsAt, now)}.{weekly}");
    }

    private static UsageFreshnessState GetFreshnessState(
        CodexUsageSnapshot snapshot,
        DateTimeOffset now,
        TimeSpan? refreshInterval)
    {
        return snapshot.Source switch
        {
            UsageDataSource.LastConfirmed => UsageFreshnessState.LastConfirmed,
            UsageDataSource.AppServer or UsageDataSource.LocalSession =>
                UsageFreshness.Classify(snapshot.UpdatedAt, now, refreshInterval ?? TimeSpan.FromMinutes(1)),
            _ => UsageFreshnessState.Unknown,
        };
    }

    private static bool IsDataAvailable(CodexUsageSnapshot snapshot, UsageFreshnessState freshness) =>
        snapshot.Source is UsageDataSource.AppServer or UsageDataSource.LocalSession
        && freshness == UsageFreshnessState.Fresh;

    private static string FormatWindowSummary(
        RateLimitWindow window,
        CodexUsageSnapshot snapshot,
        DateTimeOffset now)
    {
        var label = ReferenceEquals(window, snapshot.Secondary) && snapshot.Primary is not null
            ? "Weekly window"
            : ReferenceEquals(window, snapshot.Primary) && snapshot.Secondary is not null
                ? "5-hour window"
                : "Usage window";
        return $"{label}: {window.RemainingPercent:0}% available; resets {FormatRelativeTime(window.ResetsAt, now)}.";
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

        if (!UsageFreshness.IsValidWindow(window, now))
        {
            return $"## {name}\n\n**Window data expired or invalid; waiting for refreshed data.**";
        }

        return $"## {name}\n\n**{window.RemainingPercent:0}% available**  \nResets {FormatRelativeTime(window.ResetsAt, now)} · {FormatLocalTime(window.ResetsAt, "ddd d MMM HH:mm")}";
    }

    private static string FormatWeeklyBudget(RateLimitWindow window, DateTimeOffset now, bool dataAvailable)
    {
        if (!dataAvailable || !UsageFreshness.IsValidWindow(window, now))
        {
            return "Remaining usage budget unavailable until refreshed.";
        }

        var remaining = window.ResetsAt - now;
        return remaining.TotalDays >= 1
            ? $"To last until reset: average at most {window.RemainingPercent / remaining.TotalDays:0.#} percentage points per day."
            : remaining.TotalHours >= 1
                ? $"To last until reset: average at most {window.RemainingPercent / remaining.TotalHours:0.#} percentage points per hour."
                : $"Available until reset in {Math.Ceiling(remaining.TotalMinutes):0} min: {window.RemainingPercent:0}%.";
    }

    private static void AddWeeklyTrendData(
        JsonObject data,
        RateLimitWindow? window,
        IReadOnlyList<UsageHistoryEntry> history,
        UsageTrendAnalyzer.TrendAnalysis? trend,
        bool dataAvailable,
        bool canShowObservedHistory,
        DateTimeOffset now,
        TimeSpan maximumGap,
        LocalTokenUsageSnapshot? tokenUsage)
    {
        data["weeklyTrendAvailable"] = false;
        data["weeklyForecastStatus"] = trend?.ForecastStatus ?? "Forecast unavailable.";
        data["weeklyTrendLegend"] = "Solid: used quota (%) · dashed: forecast · breaks: gaps or restored allowance. Token bars appear with Details; their scale is independent.";
        data["weeklyRestorationAvailable"] = false;
        if (window is not { } validWindow
            || !UsageFreshness.IsValidWindow(validWindow, now)
            || !canShowObservedHistory)
        {
            return;
        }

        data["weeklyForecastStatus"] = dataAvailable && trend is not null
            ? trend.ForecastStatus
            : "Forecast unavailable until usage data is refreshed.";

        var windowStartsAt = validWindow.ResetsAt - TimeSpan.FromMinutes(validWindow.WindowMinutes);
        var chartHistory = history.Where(sample => sample.RecordedAt >= windowStartsAt).ToArray();
        if (chartHistory.Length < 2)
        {
            return;
        }

        var chart = WeeklyUsageTrendChartRenderer.Create(
            history,
            validWindow,
            now,
            maximumGap,
            dataAvailable ? trend?.Forecast : null,
            tokenUsage);
        if (chart is null)
        {
            return;
        }

        data["weeklyTrendAvailable"] = true;
        data["weeklyTrendChartUrl"] = chart.ImageUrl;
        data["weeklyTrendChartAlt"] = chart.AltText;
        var forecastLegend = dataAvailable && trend?.Forecast is not null
            ? "dashed: forecast"
            : "forecast pending sufficient fresh measurements";
        data["weeklyTrendLegend"] = tokenUsage?.Status switch
        {
            LocalTokenUsageStatus.Complete => $"Solid: used quota (%) · breaks: gaps or restorations · {forecastLegend} · bars: local tokens per day · amber: detected restorations",
            LocalTokenUsageStatus.Partial => $"Solid: used quota (%) · breaks: gaps or restorations · {forecastLegend} · bars: partial local tokens per day · amber: detected restorations",
            _ => $"Solid: used quota (%) · breaks: gaps or restorations · {forecastLegend} · local token data unavailable · amber: detected restorations",
        };

        var restorations = WeeklyAllowanceRestoration.Detect(history, validWindow, now);
        if (restorations.Length > 0)
        {
            data["weeklyRestorationAvailable"] = true;
            data["weeklyRestorationSummary"] = FormatRestorationSummary(restorations);
            if (dataAvailable && trend?.Forecast is not null)
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
        $"## Usage trend\n\n{FormatTrendBodyForReset(history, null, null, now, dataAvailable, UsageFreshness.MaximumAge(TimeSpan.FromMinutes(1)))}";

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
        var body = FormatTrendBodyForReset(
            history,
            windowStartsAt,
            window.ResetsAt,
            now,
            dataAvailable,
            maximumSampleAge,
            adaptiveWeeklyForecastEnabled,
            adaptiveWeeklyHistory);
        return window.WindowMinutes == 10080 ? $"{body}  \n{FormatWeeklyBudget(window, now, dataAvailable)}" : body;
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
        var analysis = UsageTrendAnalyzer.Analyze(
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
        var basis = resetsAt - windowStartsAt == TimeSpan.FromDays(7) ? $"  \n{analysis.ForecastStatus}" : string.Empty;
        return $"{analysis.HistoryValues}  \n{message}{basis}";
    }

    private static TimeSpan TrendFreshness(TimeSpan refreshInterval) =>
        UsageFreshness.MaximumAge(refreshInterval);

    private static TimeSpan TrendMaximumGap(TimeSpan refreshInterval) =>
        UsageTrendHistory.MaximumGap(refreshInterval);

    internal static string FormatResetSummary(RateLimitResetCredits? resets, DateTimeOffset now)
    {
        if (resets is null) return "- **Resets:** not available";
        var nextExpiry = resets.Credits?.Where(credit => credit.ExpiresAt > now).MinBy(credit => credit.ExpiresAt)?.ExpiresAt;
        var expiry = nextExpiry is null ? string.Empty : $" · next one expires {FormatRelativeTime(nextExpiry.Value, now)}";
        return $"- **Resets:** {resets.AvailableCount.ToString(CultureInfo.CurrentCulture)} available{expiry}";
    }

    internal static string FormatDataStatus(
        CodexUsageSnapshot snapshot,
        DateTimeOffset now,
        TimeSpan? refreshInterval = null)
    {
        if (snapshot.Source == UsageDataSource.Unavailable)
        {
            return snapshot.LastAttemptAt is { } lastAttempt
                ? $"Data not available · last attempt at {FormatLocalTime(lastAttempt, "HH:mm")}"
                : "Data not available · last attempt unknown";
        }

        var mode = snapshot.Source switch
        {
            UsageDataSource.AppServer => "Live",
            UsageDataSource.LocalSession => "Local fallback data",
            UsageDataSource.LastConfirmed => "Last confirmed usage",
            UsageDataSource.Initializing => "Loading data",
            UsageDataSource.Unavailable => "Data not available",
            _ => "Data not available",
        };

        var state = GetFreshnessState(snapshot, now, refreshInterval);
        var freshness = state switch
        {
            UsageFreshnessState.Fresh => $"just updated at {FormatLocalTime(snapshot.UpdatedAt, "HH:mm")}",
            UsageFreshnessState.Stale => $"possibly outdated · updated {FormatAge(snapshot.UpdatedAt, now)} ago",
            UsageFreshnessState.Future => $"timestamp is in the future ({FormatLocalTime(snapshot.UpdatedAt, "HH:mm")})",
            UsageFreshnessState.LastConfirmed => $"last confirmed {FormatAge(snapshot.UpdatedAt, now)} ago",
            _ => "data age unknown",
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

    private static string FormatAge(DateTimeOffset timestamp, DateTimeOffset now)
    {
        TimeSpan age;
        try
        {
            age = now - timestamp;
        }
        catch (ArgumentOutOfRangeException)
        {
            return "an unknown time";
        }

        if (age < TimeSpan.Zero)
        {
            return "in the future";
        }

        return age < TimeSpan.FromMinutes(1)
            ? "less than a minute"
            : age < TimeSpan.FromHours(1)
                ? $"{Math.Max(1, (int)age.TotalMinutes)} minutes"
                : age < TimeSpan.FromDays(1)
                    ? $"{(int)age.TotalHours} hours"
                    : $"{(int)age.TotalDays} days";
    }

    internal static string FormatError(CodexUsageSnapshot snapshot)
    {
        if (snapshot.Source == UsageDataSource.LocalSession && snapshot.Error is not null)
        {
            return $"> **Live Codex data is unavailable. Showing local fallback data.**  \n> {CodexUsageService.LiveDataUnavailableMessage}";
        }

        if (snapshot.Source == UsageDataSource.LastConfirmed)
        {
            return $"> **Live Codex data is unavailable. Showing last confirmed usage.**  \n> {CodexUsageService.LiveDataUnavailableMessage}";
        }

        return snapshot.Source == UsageDataSource.Unavailable
            ? $"> **Codex usage data is unavailable.**  \n> {CodexUsageService.AllDataUnavailableMessage}"
            : string.Empty;
    }

    private static string FormatLocalTime(DateTimeOffset value, string format) =>
        value.ToLocalTime().ToString(format, CultureInfo.CurrentCulture);

    private void UpdatePresentation()
    {
        lock (_presentationLock)
        {
            if (_disposed)
            {
                return;
            }

            var presentation = _usage.GetPresentation();
            var snapshot = presentation.Usage;
            var now = DateTimeOffset.Now;
            IsLoading = presentation.IsLoading;
            _mainContent.DataJson = FormatMainDataJson(
                snapshot,
                now,
                presentation.IsLoading,
                presentation.PrimaryHistory,
                presentation.WeeklyHistory,
                _usage.RefreshInterval,
                _settings.UseAdaptiveWeeklyForecast,
                presentation.AdaptiveWeeklyHistory,
                presentation.TokenUsage,
                _showDetails);
            using var mainData = JsonDocument.Parse(_mainContent.DataJson);
            var data = mainData.RootElement;
            _details.Body = FormatDetailsBody(snapshot, now, presentation.WeeklyHistory, _usage.RefreshInterval)
                + $"\n\n## Forecast and budget\n\n### 5-hour\n\n{data.GetProperty("fiveHourProjection").GetString()}\n\n### Weekly\n\n{data.GetProperty("weeklyProjection").GetString()}\n\n{data.GetProperty("weeklyBudget").GetString()}\n\n{data.GetProperty("weeklyForecastStatus").GetString()}\n\n## Chart guide\n\n{data.GetProperty("weeklyTrendLegend").GetString()}\n\nTime elapsed is a comparison with an even budget, not a forecast. Forecasts are conditional estimates. Weekly forecasts need 30 minutes of recent, continuous measurements; a gap or restored allowance starts a new series. Local tokens describe activity, not quota consumption."
                + (_usage.HistoryStorageError is { } error ? $"\n\n> **Local storage:** {error}" : string.Empty);
        }

        RaiseItemsChanged(0);
    }

    private void ToggleDetails()
    {
        lock (_presentationLock)
        {
            if (_disposed) return;
            _showDetails = !_showDetails;
            Details = _showDetails ? _details : null;
        }
        UpdatePresentation();
    }

    private void OnUpdated(object? sender, EventArgs e) => UpdatePresentation();

    internal void Refresh() => UpdatePresentation();

    public void Dispose()
    {
        lock (_presentationLock)
        {
            _disposed = true;
            _usage.Updated -= OnUpdated;
        }
        GC.SuppressFinalize(this);
    }
}


internal sealed partial class UsageDashboardForm(Action toggleDetails) : FormContent
{
    public override CommandResult SubmitForm(string payload)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > 1024) return CommandResult.KeepOpen();
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String && action.GetString() == "details")
                toggleDetails();
        }
        catch (JsonException) { }
        return CommandResult.KeepOpen();
    }
}
