using System.Globalization;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodexUsageDock;

internal sealed partial class CodexPlanningPage : ContentPage, IDisposable
{
    private readonly CodexUsageService _service;
    private readonly CodexUsageDockSettingsPage _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private MarkdownContent _content = new(string.Empty);
    private bool _disposed;

    internal CodexPlanningPage(CodexUsageService service, CodexUsageDockSettingsPage settings, Func<DateTimeOffset>? clock = null)
    {
        _service = service;
        _settings = settings;
        _clock = clock ?? (() => DateTimeOffset.Now);
        Id = "nl.mathijs.codexusage.planner";
        Name = "Open";
        Title = "Codex workday planner";
        Icon = new IconInfo("\uE787");
        service.Updated += OnUpdated;
        Refresh();
    }

    public override IContent[] GetContent() { lock (_gate) { return [_content]; } }

    internal void Refresh()
    {
        var now = _clock();
        var localEnd = now.LocalDateTime.Date.Add(_settings.WorkdayEnd.ToTimeSpan());
        var body = TimeZoneInfo.Local.IsInvalidTime(localEnd)
            ? "The selected workday end does not exist in today's local time zone. Choose another time in settings."
            : FormatPlan(UsagePlanner.Plan(_service.GetPresentation(), now, _service.RefreshInterval,
                new DateTimeOffset(localEnd, TimeZoneInfo.Local.GetUtcOffset(localEnd)), _settings.RemainingWorkdays));
        lock (_gate)
        {
            if (_disposed) return;
            _content = new MarkdownContent(body);
            Commands = [new CommandContextItem(new RefreshUsageCommand(_service)) { Title = "Refresh usage" },
                new CommandContextItem(_settings) { Title = "Change planning assumptions" },
                new CommandContextItem(new CopyTextCommand(body)) { Title = "Copy plan" }];
        }
        RaiseItemsChanged(0);
    }

    internal static string FormatPlan(UsagePlanningResult plan)
    {
        var text = new StringBuilder("# Workday planner\n\n").Append(plan.Status).Append("\n\n");
        text.Append("Target today: ").Append(plan.DesiredEnd.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture))
            .Append(". Remaining workdays before weekly reset: ").Append(plan.RequestedWorkdays?.ToString(CultureInfo.InvariantCulture) ?? "1")
            .Append(". Change these assumptions in settings.\n\n");
        foreach (var window in plan.Windows)
        {
            text.Append("## ").Append(window.Label).Append(" allowance\n\n")
                .Append(window.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture)).Append("% remaining; budget ")
                .Append(window.AvailablePointsPerWorkday.ToString("0.#", CultureInfo.InvariantCulture)).Append(" quota percentage points per workday, or ")
                .Append(window.AvailablePointsPerHour.ToString("0.#", CultureInfo.InvariantCulture)).Append(" points per hour today.\n\n")
                .Append(window.HorizonMessage).Append("\n\n")
                .Append("**Recent pace:** ").Append(window.Forecast.Status).Append("\n\n")
                .Append("**Evidence:** ").Append(window.Evidence.Summary).Append("\n\n")
                .Append("**Last observation check:** ").Append(window.Backtest.Status).Append("\n\n");
            if (window.Backtest.AbsoluteErrorPercentagePoints is { } error)
                text.Append("Held-out prediction error: ").Append(error.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append(" percentage points. One held-out observation does not establish long-term accuracy.\n\n");
        }
        return text.Append("Quota points are not tokens, money, or a guaranteed number of tasks. Both windows apply independently. ")
            .Append("This planner uses recent pace; the dashboard may additionally use its optional learned weekly pattern.").ToString();
    }

    private void OnUpdated(object? sender, EventArgs args) => Refresh();
    public void Dispose() { lock (_gate) { _disposed = true; _service.Updated -= OnUpdated; } }
}
