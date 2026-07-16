using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace CodexUsageDock;

internal sealed record WeeklyUsageTrendChart(string ImageUrl, string AltText);

internal static class WeeklyUsageTrendChartRenderer
{
    internal const int Height = 136;
    internal const int MaximumRenderedPoints = 180;

    private const double Left = 38;
    private const double Right = 6;
    private const double TrendTop = 8;
    private const double TrendHeight = 60;
    private const double DailyUseTop = 87;
    private const double DailyUseHeight = 27;
    private const double DayLabelTop = 122;
    private const int BitmapGlyphColumns = 3;
    private const int BitmapGlyphRows = 5;
    private const double BitmapCellSize = 2;
    private const double BitmapGlyphGap = 1;
    private const string AxisLabelFill = "#C8C8C8";
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly Dictionary<char, string> BitmapGlyphs = new()
    {
        ['0'] = "111101101101111",
        ['1'] = "010110010010111",
        ['2'] = "111001111100111",
        ['3'] = "111001111001111",
        ['4'] = "101101111001001",
        ['5'] = "111100111001111",
        ['6'] = "111100111101111",
        ['7'] = "111001010010010",
        ['8'] = "111101111101111",
        ['9'] = "111101111001111",
        ['A'] = "010101111101101",
        ['B'] = "110101110101110",
        ['C'] = "111100100100111",
        ['D'] = "110101101101110",
        ['E'] = "111100110100111",
        ['F'] = "111100110100100",
        ['G'] = "111100101101111",
        ['H'] = "101101111101101",
        ['I'] = "111010010010111",
        ['J'] = "001001001101111",
        ['K'] = "101101110101101",
        ['L'] = "100100100100111",
        ['M'] = "101111111101101",
        ['N'] = "101111111111101",
        ['O'] = "111101101101111",
        ['P'] = "111101111100100",
        ['Q'] = "111101101111001",
        ['R'] = "110101110101101",
        ['S'] = "111100111001111",
        ['T'] = "111010010010010",
        ['U'] = "101101101101111",
        ['V'] = "101101101101010",
        ['W'] = "101101111111101",
        ['X'] = "101101010101101",
        ['Y'] = "101101010010010",
        ['Z'] = "111001010100111",
        ['%'] = "101001010100101",
        ['?'] = "111001010000010",
    };

    internal static WeeklyUsageTrendChart? Create(
        IReadOnlyList<UsageHistoryEntry> history,
        RateLimitWindow window,
        DateTimeOffset now,
        TimeSpan maximumGap,
        UsageTrendForecast? forecast,
        CultureInfo? culture = null)
    {
        if (window.WindowMinutes <= 0)
        {
            return null;
        }

        var windowStart = window.ResetsAt - TimeSpan.FromMinutes(window.WindowMinutes);
        var effectiveNow = now < window.ResetsAt ? now : window.ResetsAt;
        if (effectiveNow <= windowStart)
        {
            return null;
        }

        var displayCulture = culture ?? CultureInfo.CurrentCulture;
        var samples = Normalize(history, windowStart, effectiveNow);
        if (samples.Length < 2)
        {
            return null;
        }

        var observedSegments = DownsampleSegments(SplitAtGaps(samples, maximumGap), windowStart, window.ResetsAt);
        var dailyUse = CalculateDailyUse(samples, windowStart, window.ResetsAt, effectiveNow, maximumGap);
        var latestSegment = observedSegments.LastOrDefault(segment => segment.Length >= 2);
        var usableForecast = latestSegment is not null && forecast is { } candidate && candidate.EndsAt > latestSegment[^1].RecordedAt
            ? candidate
            : null;

        var document = CreateDocument();
        AddTrendGrid(document);
        AddNowMarker(document, windowStart, window.ResetsAt, effectiveNow);
        AddObservedLines(document, observedSegments, windowStart, window.ResetsAt);
        AddForecastLine(document, latestSegment, usableForecast, windowStart, window.ResetsAt);
        AddDailyUseBars(document, dailyUse, displayCulture);

        var first = samples[0];
        var last = samples[^1];
        var altText = FormatAltText(first, last, samples.Length, usableForecast, windowStart, window.ResetsAt, dailyUse, displayCulture);
        return new WeeklyUsageTrendChart(UsageDashboardCard.CreateSvgImageUrl(document), altText);
    }

    private static XElement CreateDocument() => new(
        Svg + "svg",
        new XAttribute("width", UsageDashboardCard.BarWidth),
        new XAttribute("height", Height),
        new XAttribute("viewBox", $"0 0 {UsageDashboardCard.BarWidth} {Height}"),
        new XAttribute("role", "img"));

    private static UsageHistoryEntry[] Normalize(
        IReadOnlyList<UsageHistoryEntry> history,
        DateTimeOffset windowStart,
        DateTimeOffset now) =>
        history
            .Where(sample => sample.RecordedAt >= windowStart
                && sample.RecordedAt <= now
                && double.IsFinite(sample.RemainingPercent)
                && sample.RemainingPercent is >= 0 and <= 100)
            .OrderBy(sample => sample.RecordedAt)
            .GroupBy(sample => sample.RecordedAt)
            .Select(group => group.Last())
            .ToArray();

    private static List<UsageHistoryEntry[]> DownsampleSegments(
        List<UsageHistoryEntry[]> segments,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var totalSamples = segments.Sum(segment => segment.Length);
        if (totalSamples <= MaximumRenderedPoints)
        {
            return [.. segments];
        }

        var minimumPoints = segments.Select(segment => segment.Length > 1 ? 2 : 1).ToArray();
        if (minimumPoints.Sum() > MaximumRenderedPoints)
        {
            return Enumerable.Range(0, MaximumRenderedPoints)
                .Select(slot =>
                {
                    var segment = segments[(int)(slot * segments.Count / (double)MaximumRenderedPoints)];
                    return new[] { segment[^1] };
                })
                .ToList();
        }

        var result = new List<UsageHistoryEntry[]>(segments.Count);
        var remainingSamples = totalSamples;
        var remainingPoints = MaximumRenderedPoints;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var minimum = minimumPoints[index];
            var laterMinimums = minimumPoints[(index + 1)..].Sum();
            var proportional = (int)Math.Ceiling((double)segment.Length / remainingSamples * remainingPoints);
            var maximumForSegment = Math.Min(segment.Length, remainingPoints - laterMinimums);
            var selectedPoints = Math.Clamp(proportional, minimum, maximumForSegment);
            result.Add(Downsample(segment, windowStart, windowEnd, selectedPoints));
            remainingSamples -= segment.Length;
            remainingPoints -= selectedPoints;
        }

        return result;
    }

    private static UsageHistoryEntry[] Downsample(
        UsageHistoryEntry[] samples,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int maximumPoints)
    {
        if (samples.Length <= maximumPoints)
        {
            return samples.ToArray();
        }

        var duration = windowEnd - windowStart;
        if (duration <= TimeSpan.Zero)
        {
            return [samples[0], samples[^1]];
        }

        var selected = new Dictionary<int, UsageHistoryEntry>();
        foreach (var sample in samples)
        {
            var position = Math.Clamp((sample.RecordedAt - windowStart).TotalMilliseconds / duration.TotalMilliseconds, 0, 0.999999d);
            var bucket = (int)(position * Math.Max(1, maximumPoints - 2));
            selected[bucket] = sample;
        }

        var points = new List<UsageHistoryEntry> { samples[0] };
        points.AddRange(selected.OrderBy(pair => pair.Key).Select(pair => pair.Value));
        points.Add(samples[^1]);
        return points
            .DistinctBy(sample => sample.RecordedAt)
            .OrderBy(sample => sample.RecordedAt)
            .ToArray();
    }

    private static List<UsageHistoryEntry[]> SplitAtGaps(
        UsageHistoryEntry[] samples,
        TimeSpan maximumGap)
    {
        var gap = maximumGap > TimeSpan.Zero ? maximumGap : TimeSpan.FromMinutes(5);
        var segments = new List<UsageHistoryEntry[]>();
        var current = new List<UsageHistoryEntry>();
        foreach (var sample in samples)
        {
            if (current.Count > 0 && sample.RecordedAt - current[^1].RecordedAt > gap)
            {
                segments.Add([.. current]);
                current.Clear();
            }

            current.Add(sample);
        }

        if (current.Count > 0)
        {
            segments.Add([.. current]);
        }

        return segments;
    }

    private static DailyUsage[] CalculateDailyUse(
        UsageHistoryEntry[] samples,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        DateTimeOffset now,
        TimeSpan maximumGap)
    {
        var dayCount = Math.Max(1, (int)Math.Ceiling((windowEnd - windowStart).TotalDays));
        var dailyUse = Enumerable.Range(0, dayCount)
            .Select(index => new DailyUsage(windowStart.AddDays(index)))
            .ToArray();
        var gap = maximumGap > TimeSpan.Zero ? maximumGap : TimeSpan.FromMinutes(5);

        for (var index = 1; index < samples.Length; index++)
        {
            var previous = samples[index - 1];
            var current = samples[index];
            var rangeStart = previous.RecordedAt < windowStart ? windowStart : previous.RecordedAt;
            var rangeEnd = current.RecordedAt > now ? now : current.RecordedAt;
            if (rangeEnd <= rangeStart || rangeEnd - rangeStart > gap)
            {
                continue;
            }

            var observedUse = Math.Max(0, previous.RemainingPercent - current.RemainingPercent);
            var rangeDuration = rangeEnd - rangeStart;
            for (var dayIndex = 0; dayIndex < dailyUse.Length; dayIndex++)
            {
                var dayStart = dailyUse[dayIndex].Start;
                var dayEnd = dayStart.AddDays(1);
                var overlapStart = rangeStart > dayStart ? rangeStart : dayStart;
                var overlapEnd = rangeEnd < dayEnd ? rangeEnd : dayEnd;
                if (overlapEnd <= overlapStart)
                {
                    continue;
                }

                dailyUse[dayIndex].HasObservation = true;
                dailyUse[dayIndex].ConsumedPercent += observedUse * (overlapEnd - overlapStart).TotalMilliseconds / rangeDuration.TotalMilliseconds;
            }
        }

        return dailyUse;
    }

    private static void AddTrendGrid(XElement document)
    {
        foreach (var percent in new[] { 100, 50, 0 })
        {
            var y = GetTrendY(percent);
            document.Add(
                new XElement(
                    Svg + "line",
                    new XAttribute("x1", Left),
                    new XAttribute("x2", UsageDashboardCard.BarWidth - Right),
                    new XAttribute("y1", Format(y)),
                    new XAttribute("y2", Format(y)),
                    new XAttribute("stroke", "#7A7A7A"),
                    new XAttribute("stroke-opacity", "0.42"),
                    new XAttribute("stroke-width", "1")));
            AddBitmapLabel(
                document,
                $"{percent}%",
                Left - 5,
                Math.Clamp(y - BitmapLabelHeight / 2, 0, DailyUseTop - 2 - BitmapLabelHeight),
                BitmapLabelAlignment.End,
                "vertical");
        }
    }

    private static void AddBitmapLabel(
        XElement document,
        string label,
        double anchorX,
        double top,
        BitmapLabelAlignment alignment,
        string axis)
    {
        var rendered = NormalizeBitmapLabel(label);
        var width = MeasureBitmapLabel(rendered);
        var left = alignment switch
        {
            BitmapLabelAlignment.Center => anchorX - width / 2,
            BitmapLabelAlignment.End => anchorX - width,
            _ => anchorX,
        };
        var group = new XElement(
            Svg + "g",
            new XAttribute("data-axis", axis),
            new XAttribute("data-axis-label", label),
            new XAttribute("data-rendered-label", rendered));

        for (var characterIndex = 0; characterIndex < rendered.Length; characterIndex++)
        {
            var glyph = BitmapGlyphs[rendered[characterIndex]];
            var glyphLeft = left + characterIndex * (BitmapGlyphColumns * BitmapCellSize + BitmapGlyphGap);
            for (var row = 0; row < BitmapGlyphRows; row++)
            {
                for (var column = 0; column < BitmapGlyphColumns; column++)
                {
                    if (glyph[row * BitmapGlyphColumns + column] != '1')
                    {
                        continue;
                    }

                    group.Add(
                        new XElement(
                            Svg + "rect",
                            new XAttribute("x", Format(glyphLeft + column * BitmapCellSize)),
                            new XAttribute("y", Format(top + row * BitmapCellSize)),
                            new XAttribute("width", Format(BitmapCellSize)),
                            new XAttribute("height", Format(BitmapCellSize)),
                            new XAttribute("fill", AxisLabelFill)));
                }
            }
        }

        document.Add(group);
    }

    private static string NormalizeBitmapLabel(string label)
    {
        var normalized = new StringBuilder(label.Length);
        foreach (var character in label.Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var glyph = char.ToUpperInvariant(character);
            normalized.Append(BitmapGlyphs.ContainsKey(glyph) ? glyph : '?');
        }

        return normalized.Length > 0 ? normalized.ToString() : "?";
    }

    private static string FormatWeekdayLabel(DateTimeOffset day, CultureInfo culture)
    {
        var label = culture.DateTimeFormat.GetAbbreviatedDayName(day.ToLocalTime().DayOfWeek)
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrEmpty(label)
            ? CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day.ToLocalTime().DayOfWeek)
            : label;
    }

    private static double MeasureBitmapLabel(string label) =>
        label.Length * BitmapGlyphColumns * BitmapCellSize + Math.Max(0, label.Length - 1) * BitmapGlyphGap;

    private static double BitmapLabelHeight => BitmapGlyphRows * BitmapCellSize;

    private static void AddNowMarker(
        XElement document,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        DateTimeOffset now)
    {
        if (now <= windowStart || now >= windowEnd)
        {
            return;
        }

        var x = GetX(now, windowStart, windowEnd);
        document.Add(
            new XElement(
                Svg + "line",
                new XAttribute("x1", Format(x)),
                new XAttribute("x2", Format(x)),
                new XAttribute("y1", TrendTop),
                new XAttribute("y2", DailyUseTop + DailyUseHeight),
                new XAttribute("stroke", "#C8C8C8"),
                new XAttribute("stroke-opacity", "0.45"),
                new XAttribute("stroke-width", "1")));
    }

    private static void AddObservedLines(
        XElement document,
        IReadOnlyList<UsageHistoryEntry[]> segments,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var latest = segments.LastOrDefault(segment => segment.Length > 0)?[^1];
        foreach (var segment in segments.Where(segment => segment.Length >= 2))
        {
            document.Add(
                new XElement(
                    Svg + "polyline",
                    new XAttribute("points", FormatPoints(segment, windowStart, windowEnd)),
                    new XAttribute("fill", "none"),
                    new XAttribute("stroke", "#5C9EFA"),
                    new XAttribute("stroke-width", "2"),
                    new XAttribute("stroke-linecap", "round"),
                    new XAttribute("stroke-linejoin", "round")));
        }

        foreach (var sample in segments
            .Where(segment => segment.Length == 1)
            .Select(segment => segment[0])
            .Where(sample => latest is null || sample.RecordedAt != latest.RecordedAt))
        {
            document.Add(
                new XElement(
                    Svg + "circle",
                    new XAttribute("cx", Format(GetX(sample.RecordedAt, windowStart, windowEnd))),
                    new XAttribute("cy", Format(GetTrendY(sample.RemainingPercent))),
                    new XAttribute("r", "1.5"),
                    new XAttribute("fill", "#5C9EFA")));
        }

        if (latest is not null)
        {
            document.Add(
                new XElement(
                    Svg + "circle",
                    new XAttribute("cx", Format(GetX(latest.RecordedAt, windowStart, windowEnd))),
                    new XAttribute("cy", Format(GetTrendY(latest.RemainingPercent))),
                    new XAttribute("r", "3"),
                    new XAttribute("fill", "#5C9EFA"),
                    new XAttribute("stroke", "#121212"),
                    new XAttribute("stroke-width", "1")));
        }
    }

    private static void AddForecastLine(
        XElement document,
        UsageHistoryEntry[]? latestSegment,
        UsageTrendForecast? forecast,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        if (latestSegment is null || forecast is null)
        {
            return;
        }

        var last = latestSegment[^1];
        var end = forecast.EndsAt < windowEnd ? forecast.EndsAt : windowEnd;
        if (end <= last.RecordedAt)
        {
            return;
        }

        var points = new List<UsageHistoryEntry>
        {
            last,
            new(end, forecast.RemainingPercent),
        };
        if (forecast.ReachesLimitBeforeReset && end < windowEnd)
        {
            points.Add(new UsageHistoryEntry(windowEnd, 0));
        }

        document.Add(
            new XElement(
                Svg + "polyline",
                new XAttribute("points", FormatPoints(points, windowStart, windowEnd)),
                new XAttribute("fill", "none"),
                new XAttribute("stroke", "#5C9EFA"),
                new XAttribute("stroke-width", "2"),
                new XAttribute("stroke-linecap", "round"),
                new XAttribute("stroke-linejoin", "round"),
                new XAttribute("stroke-dasharray", "5 4"),
                new XAttribute("stroke-opacity", "0.9")));
    }

    private static void AddDailyUseBars(
        XElement document,
        IReadOnlyList<DailyUsage> dailyUse,
        CultureInfo culture)
    {
        var chartWidth = UsageDashboardCard.BarWidth - Left - Right;
        var dayWidth = chartWidth / dailyUse.Count;
        var baseline = DailyUseTop + DailyUseHeight;
        document.Add(
            new XElement(
                Svg + "line",
                new XAttribute("x1", Left),
                new XAttribute("x2", UsageDashboardCard.BarWidth - Right),
                new XAttribute("y1", Format(baseline)),
                    new XAttribute("y2", Format(baseline)),
                    new XAttribute("stroke", "#7A7A7A"),
                    new XAttribute("stroke-opacity", "0.65"),
                    new XAttribute("stroke-width", "1")));

        for (var index = 0; index < dailyUse.Count; index++)
        {
            var day = dailyUse[index];
            var x = Left + index * dayWidth + 3;
            var width = Math.Max(2, dayWidth - 6);
            if (day.HasObservation && day.ConsumedPercent > 0)
            {
                var height = Math.Max(1, Math.Clamp(day.ConsumedPercent, 0, 100) / 100 * DailyUseHeight);
                document.Add(
                    new XElement(
                        Svg + "rect",
                        new XAttribute("x", Format(x)),
                        new XAttribute("y", Format(baseline - height)),
                        new XAttribute("width", Format(width)),
                        new XAttribute("height", Format(height)),
                        new XAttribute("rx", "1.5"),
                        new XAttribute("fill", "#7A7A7A"),
                        new XAttribute("fill-opacity", "0.85"),
                        new XAttribute("data-series", "daily-use")));
            }

            AddBitmapLabel(
                document,
                FormatWeekdayLabel(day.Start, culture),
                x + width / 2,
                DayLabelTop,
                BitmapLabelAlignment.Center,
                "horizontal");
        }
    }

    private static string FormatAltText(
        UsageHistoryEntry first,
        UsageHistoryEntry last,
        int sampleCount,
        UsageTrendForecast? forecast,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyList<DailyUsage> dailyUse,
        CultureInfo culture)
    {
        var period = $"{windowStart.ToLocalTime().ToString("ddd d MMM HH:mm", culture)} to {windowEnd.ToLocalTime().ToString("ddd d MMM HH:mm", culture)}";
        var daily = dailyUse.Any(day => day.HasObservation)
            ? "Daily bars show observed quota consumption."
            : "No continuous measurements are available for daily consumption bars.";
        var forecastText = forecast switch
        {
            { ReachesLimitBeforeReset: true } => $" Forecast reaches the limit around {forecast.EndsAt.ToLocalTime().ToString("ddd d MMM HH:mm", culture)}.",
            { } => $" Forecast leaves {forecast.RemainingPercent:0}% at reset.",
            null => " Forecast is unavailable.",
        };
        return $"Weekly quota trend from {period}. Remaining allowance changed from {first.RemainingPercent:0}% to {last.RemainingPercent:0}% across {sampleCount} observations. The vertical scale is remaining allowance from 0% to 100%; horizontal labels are quota-window weekdays. Solid line and points are observed; dashed line is forecast.{forecastText} {daily}";
    }

    private static string FormatPoints(
        IEnumerable<UsageHistoryEntry> points,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd) =>
        string.Join(
            " ",
            points.Select(point => $"{Format(GetX(point.RecordedAt, windowStart, windowEnd))},{Format(GetTrendY(point.RemainingPercent))}"));

    private static double GetX(DateTimeOffset recordedAt, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var duration = windowEnd - windowStart;
        if (duration <= TimeSpan.Zero)
        {
            return Left;
        }

        var position = Math.Clamp((recordedAt - windowStart).TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
        return Left + position * (UsageDashboardCard.BarWidth - Left - Right);
    }

    private static double GetTrendY(double remainingPercent) =>
        TrendTop + (100 - Math.Clamp(remainingPercent, 0, 100)) / 100 * TrendHeight;

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private enum BitmapLabelAlignment
    {
        Start,
        Center,
        End,
    }

    private sealed class DailyUsage(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; } = start;

        public double ConsumedPercent { get; set; }

        public bool HasObservation { get; set; }
    }
}
