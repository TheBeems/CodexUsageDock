using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class CodexUsageDockPage : ContentPage, IDisposable
{
    private readonly CodexUsageService _usage;

    public CodexUsageDockPage(CodexUsageService usage)
    {
        _usage = usage;
        _usage.Updated += OnUpdated;
        Id = "nl.mathijs.codexusage.details";
        Icon = new IconInfo("\uE943");
        Title = "Codex Usage";
        Name = "Open";
        Commands =
        [
            new CommandContextItem(new RefreshUsageCommand(_usage))
            {
                Title = "Nu vernieuwen",
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
                # Codex Usage

                {FormatSummary(snapshot, now)}

                {FormatWindow("5-uursvenster", snapshot.Primary, now)}

                {FormatWindow("Weekvenster", snapshot.Secondary, now)}

                {FormatTrend(_usage.PrimaryHistory, now)}

                ## Resets en credits

                {FormatResetSummary(snapshot.ResetCredits, now)}
                {FormatResetCredits(snapshot.ResetCredits)}
                - **Credits:** {FormatCredits(snapshot.Credits)}

                ## Account en gegevens

                - **Abonnement:** {FormatPlan(snapshot.PlanType)}
                - **Status:** {FormatDataStatus(snapshot, now)}
                - **Bron:** {snapshot.Source}
                {FormatError(snapshot.Error)}
                """),
        ];
    }

    internal static string FormatSummary(CodexUsageSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Primary is null)
        {
            return $"> ⚪ **Gebruiksruimte onbekend**  \n> {FormatDataStatus(snapshot, now)}";
        }

        var remaining = snapshot.Primary.RemainingPercent;
        var (icon, title) = remaining switch
        {
            <= 10 => ("🔴", "Bijna aan je limiet"),
            <= 30 => ("🟠", "Beperkte ruimte beschikbaar"),
            _ => ("🟢", "Ruim voldoende ruimte"),
        };
        var weekly = snapshot.Secondary is null ? string.Empty : $" Weekbudget: {snapshot.Secondary.RemainingPercent:0}%.";
        return $"> {icon} **{title}**  \n> {remaining:0}% beschikbaar; reset {FormatRelativeTime(snapshot.Primary.ResetsAt, now)}.{weekly}";
    }

    internal static string FormatCredits(CreditBalance? credits)
    {
        if (credits is null)
        {
            return "niet beschikbaar";
        }

        if (credits.Unlimited)
        {
            return "Onbeperkt";
        }

        return credits.HasCredits && !string.IsNullOrWhiteSpace(credits.Balance)
            ? credits.Balance
            : "geen beschikbaar saldo";
    }

    internal static string FormatResetCredits(RateLimitResetCredits? resets)
    {
        if (resets?.Credits is not { Count: > 0 } credits)
        {
            return string.Empty;
        }

        var lines = credits.Select((credit, index) =>
        {
            var title = string.IsNullOrWhiteSpace(credit.Title) ? $"Reset {index + 1}" : credit.Title;
            var expiry = credit.ExpiresAt is null
                ? "vervaldatum onbekend"
                : $"verloopt {credit.ExpiresAt.Value.ToLocalTime():ddd d MMM HH:mm}";
            return $"  - **{title}:** {expiry}";
        }).ToList();

        if (resets.AvailableCount > credits.Count)
        {
            lines.Add($"  - Voor {resets.AvailableCount - credits.Count} reset(s) zijn geen vervaldetails beschikbaar.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatWindow(string name, RateLimitWindow? window, DateTimeOffset now)
    {
        if (window is null)
        {
            return $"## {name}\n\n⚪ Niet beschikbaar";
        }

        return $"## {name}\n\n{ProgressBar(window.RemainingPercent)} **{window.RemainingPercent:0}% beschikbaar**  \nReset {FormatRelativeTime(window.ResetsAt, now)} · {window.ResetsAt.ToLocalTime():ddd d MMM HH:mm}";
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
            return "nu";
        }

        if (duration < TimeSpan.FromMinutes(1)) return "over minder dan een minuut";
        if (duration < TimeSpan.FromHours(1)) return $"over {(int)Math.Ceiling(duration.TotalMinutes)} minuten";
        if (duration < TimeSpan.FromHours(24))
        {
            var hours = (int)duration.TotalHours;
            var minutes = duration.Minutes;
            return minutes == 0 ? $"over {hours} uur" : $"over {hours} uur en {minutes} minuten";
        }

        var local = target.ToLocalTime();
        if (local.Date == now.ToLocalTime().Date.AddDays(1)) return $"morgen om {local:HH:mm}";
        return $"over {(int)Math.Ceiling(duration.TotalDays)} dagen";
    }

    internal static string FormatTrend(IReadOnlyList<UsageHistoryEntry> history, DateTimeOffset now)
    {
        if (history.Count < 2)
        {
            return "## Gebruikstrend\n\nNog onvoldoende historie voor een schatting.";
        }

        var samples = history.Count <= 5 ? history : history.Where((_, index) => index % Math.Max(1, history.Count / 4) == 0).Take(4).Append(history[^1]).ToArray();
        var values = string.Join(" → ", samples.Select(sample => $"{sample.RemainingPercent:0}%"));
        var first = history[0];
        var last = history[^1];
        var elapsedMinutes = (last.RecordedAt - first.RecordedAt).TotalMinutes;
        var consumed = first.RemainingPercent - last.RemainingPercent;
        if (elapsedMinutes < 2 || consumed <= 0.5)
        {
            return $"## Gebruikstrend\n\n{values}  \nNog onvoldoende verandering voor een betrouwbare schatting.";
        }

        var minutesToEmpty = last.RemainingPercent / (consumed / elapsedMinutes);
        var estimated = now.AddMinutes(minutesToEmpty);
        return $"## Gebruikstrend\n\n{values}  \n*Schatting bij huidig tempo: limiet rond {estimated.ToLocalTime():HH:mm}.*";
    }

    internal static string FormatResetSummary(RateLimitResetCredits? resets, DateTimeOffset now)
    {
        if (resets is null) return "- **Resets:** niet beschikbaar";
        var nextExpiry = resets.Credits?.Where(credit => credit.ExpiresAt > now).MinBy(credit => credit.ExpiresAt)?.ExpiresAt;
        var expiry = nextExpiry is null ? string.Empty : $" · eerstvolgende vervalt {FormatRelativeTime(nextExpiry.Value, now)}";
        return $"- **Resets:** {resets.AvailableCount.ToString(CultureInfo.CurrentCulture)} beschikbaar{expiry}";
    }

    internal static string FormatDataStatus(CodexUsageSnapshot snapshot, DateTimeOffset now)
    {
        var age = now - snapshot.UpdatedAt;
        var freshness = age < TimeSpan.FromMinutes(2)
            ? $"zojuist bijgewerkt om {snapshot.UpdatedAt.ToLocalTime():HH:mm}"
            : $"mogelijk verouderd · {FormatRelativeAge(age)} geleden bijgewerkt";
        var mode = snapshot.Source == "Codex app-server" ? "Live" : "Lokale reservegegevens";
        return $"{mode} · {freshness}";
    }

    internal static string FormatPlan(string? plan) => string.IsNullOrWhiteSpace(plan)
        ? "Onbekend"
        : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(plan.Replace('_', ' '));

    private static string FormatRelativeAge(TimeSpan age) => age < TimeSpan.FromHours(1)
        ? $"{Math.Max(1, (int)age.TotalMinutes)} minuten"
        : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} uur" : $"{(int)age.TotalDays} dagen";

    private static string FormatError(string? error) => error is null ? string.Empty : $"> ⚠ Technisch detail: {error}";

    private void OnUpdated(object? sender, EventArgs e) => RaiseItemsChanged(0);

    public void Dispose()
    {
        _usage.Updated -= OnUpdated;
        GC.SuppressFinalize(this);
    }
}
