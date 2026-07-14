using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class CodexUsageDockPage : ContentPage, IDisposable
{
    private static readonly string Version = CodexUsageDockMetadata.Version;
    private readonly CodexUsageService _usage;

    public CodexUsageDockPage(CodexUsageService usage)
    {
        _usage = usage;
        _usage.Updated += OnUpdated;
        Id = "nl.mathijs.codexusage.details";
        Title = $"Codex Usage - {Version}";
        Name = "Open";
        Commands =
        [
            new CommandContextItem(new RefreshUsageCommand(_usage))
            {
                Title = "Refresh now",
            },
        ];
    }

    public override IContent[] GetContent()
    {
        var snapshot = _usage.Current;
        var now = DateTimeOffset.Now;
        return
        [
            new MarkdownContent($"""
                # Codex Usage - {Version}

                {FormatRefreshStatus(_usage.IsLoading)}

                {FormatSummary(snapshot, now)}

                {FormatWindow("5-hour window", snapshot.Primary, now, snapshot.Secondary is not null ? "Currently inactive" : null)}

                {FormatWindow("Weekly window", snapshot.Secondary, now)}

                {FormatTrend("5-hour usage trend", _usage.PrimaryHistory, snapshot.Primary, now, snapshot.Source != UsageDataSource.Unavailable, TrendFreshness(_usage.RefreshInterval))}

                {FormatTrend("Weekly usage trend", _usage.WeeklyHistory, snapshot.Secondary, now, snapshot.Source != UsageDataSource.Unavailable, TrendFreshness(_usage.RefreshInterval))}

                ## Resets and credits

                {FormatResetSummary(snapshot.ResetCredits, now)}
                {FormatResetCredits(snapshot.ResetCredits)}
                - **Credits:** {FormatCredits(snapshot.Credits)}

                ## Account and data

                - **Plan:** {FormatPlan(snapshot.PlanType)}
                - **Status:** {FormatDataStatus(snapshot, now)}
                - **Source:** {snapshot.SourceDisplayName}
                {FormatError(snapshot)}
                """),
        ];
    }

    internal static string FormatRefreshStatus(bool isLoading) => isLoading
        ? "> ⏳ **Refreshing Codex usage…**"
        : string.Empty;

    internal static string FormatSummary(CodexUsageSnapshot snapshot, DateTimeOffset now)
    {
        var activeWindow = snapshot.Primary ?? snapshot.Secondary;
        if (activeWindow is null)
        {
            return $"> ⚪ **Usage allowance unknown**  \n> {FormatDataStatus(snapshot, now)}";
        }

        var remaining = activeWindow.RemainingPercent;
        var (icon, title) = remaining switch
        {
            <= 10 => ("🔴", "Almost at your limit"),
            <= 30 => ("🟠", "Limited allowance available"),
            _ => ("🟢", "Plenty of allowance available"),
        };
        var weekly = snapshot.Primary is not null && snapshot.Secondary is not null
            ? $" Weekly allowance: {snapshot.Secondary.RemainingPercent:0}%."
            : string.Empty;
        return $"> {icon} **{title}**  \n> {remaining:0}% available; resets {FormatRelativeTime(activeWindow.ResetsAt, now)}.{weekly}";
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
            return $"## {name}\n\n⚪ {inactiveMessage ?? "Not available"}";
        }

        return $"## {name}\n\n{ProgressBar(window.RemainingPercent)} **{window.RemainingPercent:0}% available**  \nResets {FormatRelativeTime(window.ResetsAt, now)} · {FormatLocalTime(window.ResetsAt, "ddd d MMM HH:mm")}";
    }

    internal static string ProgressBar(double remainingPercent)
    {
        const int width = 10;
        var filled = Math.Clamp((int)Math.Round(remainingPercent / 100 * width), 0, width);
        return new string('█', filled) + new string('░', width - filled);
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
        FormatTrendForReset("Usage trend", history, null, null, now, dataAvailable, TimeSpan.FromMinutes(5));

    internal static string FormatTrend(
        string title,
        IReadOnlyList<UsageHistoryEntry> history,
        RateLimitWindow? window,
        DateTimeOffset now,
        bool dataAvailable,
        TimeSpan maximumSampleAge)
    {
        if (window is null)
        {
            return string.Empty;
        }

        var windowStartsAt = window.ResetsAt - TimeSpan.FromMinutes(window.WindowMinutes);
        return FormatTrendForReset(title, history, windowStartsAt, window.ResetsAt, now, dataAvailable, maximumSampleAge);
    }

    private static string FormatTrendForReset(
        string title,
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset? windowStartsAt,
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        bool dataAvailable,
        TimeSpan maximumSampleAge)
    {
        if (!dataAvailable)
        {
            return $"## {title}\n\nUnavailable until a fresh usage sample is loaded.";
        }

        var historyInCurrentWindow = windowStartsAt is { } start
            ? history.Where(sample => sample.RecordedAt >= start).ToArray()
            : history;

        var segmentStart = 0;
        for (var index = 1; index < historyInCurrentWindow.Count; index++)
        {
            if (historyInCurrentWindow[index].RemainingPercent > historyInCurrentWindow[index - 1].RemainingPercent)
            {
                segmentStart = index;
            }
        }

        var currentWindow = historyInCurrentWindow.Skip(segmentStart).ToArray();
        if (currentWindow.Length < 2)
        {
            return $"## {title}\n\nNot enough history for an estimate yet.";
        }

        var samples = currentWindow.Length <= 5 ? currentWindow : currentWindow.Where((_, index) => index % Math.Max(1, currentWindow.Length / 4) == 0).Take(4).Append(currentWindow[^1]).ToArray();
        var values = string.Join(" → ", samples.Select(sample => $"{sample.RemainingPercent:0}%"));
        var first = currentWindow[0];
        var last = currentWindow[^1];
        var elapsedMinutes = (last.RecordedAt - first.RecordedAt).TotalMinutes;
        var consumed = first.RemainingPercent - last.RemainingPercent;
        if (elapsedMinutes < 2 || consumed <= 0.5)
        {
            return $"## {title}\n\n{values}  \nNot enough change for a reliable estimate yet.";
        }

        if (now - last.RecordedAt > maximumSampleAge)
        {
            return $"## {title}\n\n{values}  \nThe latest measurement is too old for a reliable estimate.";
        }

        var minutesToEmpty = last.RemainingPercent / (consumed / elapsedMinutes);
        var estimated = last.RecordedAt.AddMinutes(minutesToEmpty);
        if (resetsAt is { } reset && estimated >= reset)
        {
            var remainingAtReset = Math.Max(0, last.RemainingPercent - (consumed / elapsedMinutes) * (reset - last.RecordedAt).TotalMinutes);
            return $"## {title}\n\n{values}  \n*Estimate at the current rate: {remainingAtReset:0}% available at reset.*";
        }

        return $"## {title}\n\n{values}  \n*Estimate at the current rate: limit around {FormatLocalTime(estimated, "HH:mm")}.*";
    }

    private static TimeSpan TrendFreshness(TimeSpan refreshInterval) =>
        refreshInterval > TimeSpan.FromMinutes(5) ? refreshInterval : TimeSpan.FromMinutes(5);

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
            return $"> ⚠ **Live Codex data is unavailable. Showing local fallback data.**  \n> {CodexUsageService.LiveDataUnavailableMessage}";
        }

        return snapshot.Source == UsageDataSource.Unavailable
            ? $"> ⚠ **Codex usage data is unavailable.**  \n> {CodexUsageService.AllDataUnavailableMessage}"
            : string.Empty;
    }

    private static string FormatLocalTime(DateTimeOffset value, string format) =>
        value.ToLocalTime().ToString(format, CultureInfo.CurrentCulture);

    private void OnUpdated(object? sender, EventArgs e) => RaiseItemsChanged(0);

    public void Dispose()
    {
        _usage.Updated -= OnUpdated;
        GC.SuppressFinalize(this);
    }
}
