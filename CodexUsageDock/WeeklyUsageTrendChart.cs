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
    private const double Right = 38;
    private const double TrendTop = 8;
    private const double TrendBottom = 114;
    private const double TrendHeight = TrendBottom - TrendTop;
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
        [' '] = "000000000000000",
        ['%'] = "101001010100101",
        ['.'] = "000000000000010",
        ['?'] = "111001010000010",
    };

    internal static WeeklyUsageTrendChart? Create(
        IReadOnlyList<UsageHistoryEntry> history,
        RateLimitWindow window,
        DateTimeOffset now,
        TimeSpan maximumGap,
        UsageTrendForecast? forecast,
        LocalTokenUsageSnapshot? tokenUsage = null,
        CultureInfo? culture = null,
        TimeZoneInfo? timeZone = null)
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
        var displayTimeZone = timeZone ?? TimeZoneInfo.Local;
        var restorations = WeeklyAllowanceRestoration.Detect(history, window, effectiveNow);
        var samples = Normalize(history, windowStart, effectiveNow);
        if (samples.Length < 2)
        {
            return null;
        }

        var dailyUse = CreateCalendarDays(windowStart, window.ResetsAt, effectiveNow, displayTimeZone);
        ApplyDailyTokens(dailyUse, tokenUsage);
        var tokenScaleMaximum = GetTokenScaleMaximum(dailyUse);
        var calendarScale = new CalendarDayScale(dailyUse);
        var forecastSegments = SplitAtGapsOrQuotaIncreases(samples, maximumGap);
        var renderedSegments = DownsampleSegments(SplitAtQuotaIncreases(samples), windowStart, window.ResetsAt);
        var latestSegment = forecastSegments.LastOrDefault();
        var forecastSegment = latestSegment is { Length: >= 2 } ? latestSegment : null;
        var usableForecast = forecastSegment is not null && forecast is { } candidate && candidate.EndsAt > forecastSegment[^1].RecordedAt
            ? candidate
            : null;

        var document = CreateDocument();
        AddTrendGrid(document);
        AddTokenAxis(document, tokenScaleMaximum);
        AddCalendarDayGrid(document, dailyUse, calendarScale);
        AddDailyTokenBars(document, dailyUse, calendarScale, displayCulture, tokenScaleMaximum);
        AddResetMarkers(document, windowStart, window.ResetsAt, calendarScale);
        AddNowMarker(document, windowStart, window.ResetsAt, effectiveNow, calendarScale);
        AddRestorationMarkers(document, restorations, calendarScale);
        AddObservedLines(document, renderedSegments, calendarScale);
        AddForecastLine(document, forecastSegment, usableForecast, window.ResetsAt, calendarScale);

        var first = samples[0];
        var last = samples[^1];
        var altText = FormatAltText(
            first,
            last,
            samples.Length,
            usableForecast,
            restorations,
            windowStart,
            window.ResetsAt,
            dailyUse,
            tokenUsage,
            tokenScaleMaximum,
            displayCulture,
            displayTimeZone);
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

    private static List<UsageHistoryEntry[]> SplitAtGapsOrQuotaIncreases(
        UsageHistoryEntry[] samples,
        TimeSpan maximumGap)
    {
        var gap = maximumGap > TimeSpan.Zero ? maximumGap : TimeSpan.FromMinutes(5);
        return SplitAtDiscontinuities(
            samples,
            (previous, current) => current.RecordedAt - previous.RecordedAt > gap ||
                WeeklyAllowanceRestoration.IsIncrease(previous, current));
    }

    private static List<UsageHistoryEntry[]> SplitAtQuotaIncreases(UsageHistoryEntry[] samples) =>
        SplitAtDiscontinuities(
            samples,
            WeeklyAllowanceRestoration.IsIncrease);

    private static List<UsageHistoryEntry[]> SplitAtDiscontinuities(
        UsageHistoryEntry[] samples,
        Func<UsageHistoryEntry, UsageHistoryEntry, bool> startsNewSegment)
    {
        var segments = new List<UsageHistoryEntry[]>();
        var current = new List<UsageHistoryEntry>();
        foreach (var sample in samples)
        {
            if (current.Count > 0 && startsNewSegment(current[^1], sample))
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

    private static DailyUsage[] CreateCalendarDays(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(windowStart, timeZone).Date);
        var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(windowEnd, timeZone).Date);
        var days = new List<DailyUsage>();
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var localStart = AtLocalMidnight(date, timeZone);
            var localEnd = AtLocalMidnight(date.AddDays(1), timeZone);
            var start = localStart > windowStart ? localStart : windowStart;
            var end = localEnd < windowEnd ? localEnd : windowEnd;
            if (end > start)
            {
                days.Add(new DailyUsage(
                    date,
                    localStart,
                    localEnd,
                    start,
                    end,
                    start != localStart || end != localEnd,
                    now >= start && now < end));
            }
        }

        return [.. days];
    }

    private static DateTimeOffset AtLocalMidnight(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }

    private static void ApplyDailyTokens(
        IReadOnlyList<DailyUsage> days,
        LocalTokenUsageSnapshot? tokenUsage)
    {
        if (tokenUsage is null || tokenUsage.Status == LocalTokenUsageStatus.Unavailable)
        {
            return;
        }

        var totals = tokenUsage.Days.ToDictionary(day => day.Date, day => day.TotalTokens);
        foreach (var day in days)
        {
            day.HasTokenData = true;
            day.TotalTokens = Math.Max(0, totals.GetValueOrDefault(day.Date));
        }
    }

    private static long GetTokenScaleMaximum(IReadOnlyList<DailyUsage> days)
    {
        var maximum = days.Max(day => day.TotalTokens);
        if (maximum <= 0)
        {
            return 0;
        }

        var magnitude = (long)Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        var normalized = maximum / (double)magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return checked(nice * magnitude);
    }

    private static void AddCalendarDayGrid(
        XElement document,
        IReadOnlyList<DailyUsage> dailyUse,
        CalendarDayScale calendarScale)
    {
        for (var index = 1; index < dailyUse.Count; index++)
        {
            var day = dailyUse[index];
            var x = calendarScale.GetDayStartX(index);
            document.Add(
                new XElement(
                    Svg + "line",
                    new XAttribute("x1", Format(x)),
                    new XAttribute("x2", Format(x)),
                    new XAttribute("y1", TrendTop),
                    new XAttribute("y2", TrendBottom),
                    new XAttribute("stroke", "#7A7A7A"),
                    new XAttribute("stroke-opacity", "0.25"),
                    new XAttribute("stroke-width", "1"),
                    new XAttribute("data-grid", "calendar-day"),
                    new XAttribute("data-date", day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        }
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
                    new XAttribute("stroke-width", "1"),
                    new XAttribute("data-grid", "remaining-percent"),
                    new XAttribute("data-value", percent)));
            AddBitmapLabel(
                document,
                $"{percent}%",
                Left - 5,
                Math.Clamp(y - BitmapLabelHeight / 2, 0, Height - BitmapLabelHeight),
                BitmapLabelAlignment.End,
                "vertical");
        }
    }

    private static void AddTokenAxis(XElement document, long tokenScaleMaximum)
    {
        if (tokenScaleMaximum <= 0)
        {
            return;
        }

        foreach (var (tokens, y) in new[]
        {
            // Keep the top token label below the reset label at the chart boundary.
            (tokenScaleMaximum, TrendTop + BitmapLabelHeight + 2),
            (tokenScaleMaximum / 2d, TrendTop + TrendHeight / 2),
        })
        {
            AddBitmapLabel(
                document,
                FormatCompactTokens(tokens),
                UsageDashboardCard.BarWidth - Right + 5,
                Math.Clamp(y - BitmapLabelHeight / 2, 0, Height - BitmapLabelHeight),
                BitmapLabelAlignment.Start,
                "tokens");
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

    private static bool CanRenderBitmapLabel(string label)
    {
        var hasGlyph = false;
        foreach (var character in label.Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            hasGlyph = true;
            if (!BitmapGlyphs.ContainsKey(char.ToUpperInvariant(character)))
            {
                return false;
            }
        }

        return hasGlyph;
    }

    private static string FormatCalendarDayLabel(DateOnly date, CultureInfo culture)
    {
        var label = culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek)
            .Trim()
            .TrimEnd('.');
        var weekday = CanRenderBitmapLabel(label)
            ? label
            : CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek);
        return $"{weekday} {date.Day}";
    }

    private static double MeasureBitmapLabel(string label) =>
        label.Length * BitmapGlyphColumns * BitmapCellSize + Math.Max(0, label.Length - 1) * BitmapGlyphGap;

    private static double BitmapLabelHeight => BitmapGlyphRows * BitmapCellSize;

    private static void AddResetMarkers(
        XElement document,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CalendarDayScale calendarScale)
    {
        foreach (var (x, edge) in new[]
        {
            (calendarScale.GetX(windowStart), "start"),
            (calendarScale.GetX(windowEnd), "end"),
        })
        {
            document.Add(
                new XElement(
                    Svg + "line",
                    new XAttribute("x1", Format(x)),
                    new XAttribute("x2", Format(x)),
                    new XAttribute("y1", TrendTop),
                    new XAttribute("y2", TrendBottom),
                    new XAttribute("stroke", "#C8C8C8"),
                    new XAttribute("stroke-opacity", "0.6"),
                    new XAttribute("stroke-width", "1"),
                    new XAttribute("stroke-dasharray", "2 2"),
                    new XAttribute("data-marker", $"reset-{edge}")));
            AddBitmapLabel(document, "RESET", x, 0, BitmapLabelAlignment.Center, "marker");
        }
    }

    private static void AddNowMarker(
        XElement document,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        DateTimeOffset now,
        CalendarDayScale calendarScale)
    {
        if (now <= windowStart || now >= windowEnd)
        {
            return;
        }

        var x = calendarScale.GetX(now);
        document.Add(
            new XElement(
                Svg + "line",
                new XAttribute("x1", Format(x)),
                new XAttribute("x2", Format(x)),
                new XAttribute("y1", TrendTop),
                new XAttribute("y2", TrendBottom),
                new XAttribute("stroke", "#C8C8C8"),
                new XAttribute("stroke-opacity", "0.45"),
                new XAttribute("stroke-width", "1"),
                new XAttribute("data-marker", "now")));
        var labelWidth = MeasureBitmapLabel("NOW");
        var labelX = Math.Clamp(x, Left + labelWidth / 2, UsageDashboardCard.BarWidth - Right - labelWidth / 2);
        AddBitmapLabel(document, "NOW", labelX, 0, BitmapLabelAlignment.Center, "marker");
    }

    private static void AddRestorationMarkers(
        XElement document,
        AllowanceRestoration[] restorations,
        CalendarDayScale calendarScale)
    {
        foreach (var restoration in restorations)
        {
            var x = calendarScale.GetX(restoration.DetectedAt);
            var y = GetTrendY(restoration.CurrentRemainingPercent);
            document.Add(
                new XElement(
                    Svg + "line",
                    new XAttribute("x1", Format(x)),
                    new XAttribute("x2", Format(x)),
                    new XAttribute("y1", TrendTop),
                    new XAttribute("y2", TrendBottom),
                    new XAttribute("stroke", "#F2C94C"),
                    new XAttribute("stroke-opacity", "0.8"),
                    new XAttribute("stroke-width", "1.5"),
                    new XAttribute("stroke-dasharray", "3 2"),
                    new XAttribute("data-marker", "allowance-restored"),
                    new XAttribute("data-detected-at", restoration.DetectedAt.ToString("O", CultureInfo.InvariantCulture))),
                new XElement(
                    Svg + "circle",
                    new XAttribute("cx", Format(x)),
                    new XAttribute("cy", Format(y)),
                    new XAttribute("r", "3"),
                    new XAttribute("fill", "#F2C94C"),
                    new XAttribute("stroke", "#121212"),
                    new XAttribute("stroke-width", "1"),
                    new XAttribute("data-marker", "allowance-restored-point"),
                    new XAttribute("data-increase", Format(restoration.IncreasePercent))));
        }
    }

    private static void AddObservedLines(
        XElement document,
        IReadOnlyList<UsageHistoryEntry[]> segments,
        CalendarDayScale calendarScale)
    {
        var latest = segments.LastOrDefault(segment => segment.Length > 0)?[^1];
        foreach (var segment in segments.Where(segment => segment.Length >= 2))
        {
            document.Add(
                new XElement(
                    Svg + "polyline",
                    new XAttribute("points", FormatPoints(segment, calendarScale)),
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
                    new XAttribute("cx", Format(calendarScale.GetX(sample.RecordedAt))),
                    new XAttribute("cy", Format(GetTrendY(sample.RemainingPercent))),
                    new XAttribute("r", "1.5"),
                    new XAttribute("fill", "#5C9EFA")));
        }

        if (latest is not null)
        {
            document.Add(
                new XElement(
                    Svg + "circle",
                    new XAttribute("cx", Format(calendarScale.GetX(latest.RecordedAt))),
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
        DateTimeOffset windowEnd,
        CalendarDayScale calendarScale)
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

        var points = new List<UsageHistoryEntry> { last };
        if (forecast.Points is { Count: > 0 })
        {
            points.AddRange(
                forecast.Points
                    .Where(point => point.RecordedAt > last.RecordedAt && point.RecordedAt <= end)
                    .Select(point => new UsageHistoryEntry(point.RecordedAt, point.RemainingPercent)));
        }

        if (points.Count == 1 || points[^1].RecordedAt < end)
        {
            points.Add(new UsageHistoryEntry(end, forecast.RemainingPercent));
        }

        if (forecast.ReachesLimitBeforeReset && end < windowEnd)
        {
            points.Add(new UsageHistoryEntry(windowEnd, 0));
        }

        document.Add(
            new XElement(
                Svg + "polyline",
                new XAttribute("points", FormatPoints(points, calendarScale)),
                new XAttribute("fill", "none"),
                new XAttribute("stroke", "#5C9EFA"),
                new XAttribute("stroke-width", "2"),
                new XAttribute("stroke-linecap", "round"),
                new XAttribute("stroke-linejoin", "round"),
                new XAttribute("stroke-dasharray", "5 4"),
                new XAttribute("stroke-opacity", "0.9")));
    }

    private static void AddDailyTokenBars(
        XElement document,
        IReadOnlyList<DailyUsage> dailyUse,
        CalendarDayScale calendarScale,
        CultureInfo culture,
        long tokenScaleMaximum)
    {
        var baseline = TrendBottom;

        for (var index = 0; index < dailyUse.Count; index++)
        {
            var day = dailyUse[index];
            var dayLeft = calendarScale.GetDayStartX(index);
            var dayRight = calendarScale.GetDayEndX(index);
            var x = dayLeft + 3;
            var width = Math.Max(2, dayRight - dayLeft - 6);
            if (day.HasTokenData && day.TotalTokens > 0 && tokenScaleMaximum > 0)
            {
                var height = Math.Max(1, Math.Clamp(day.TotalTokens / (double)tokenScaleMaximum, 0, 1) * TrendHeight);
                document.Add(
                    new XElement(
                        Svg + "rect",
                        new XAttribute("x", Format(x)),
                        new XAttribute("y", Format(baseline - height)),
                        new XAttribute("width", Format(width)),
                        new XAttribute("height", Format(height)),
                        new XAttribute("rx", "1.5"),
                        new XAttribute("fill", "#7A7A7A"),
                        new XAttribute("fill-opacity", "0.45"),
                        new XAttribute("data-series", "daily-tokens"),
                        new XAttribute("data-date", day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        new XAttribute("data-tokens", day.TotalTokens),
                        new XAttribute("data-partial", day.IsPartial),
                        new XAttribute("data-current", day.IsCurrent),
                        new XAttribute("stroke", day.IsCurrent ? "#C8C8C8" : "none"),
                        new XAttribute("stroke-opacity", day.IsCurrent ? "0.45" : "0"),
                        new XAttribute("stroke-width", day.IsCurrent ? "1" : "0")));
            }

            AddBitmapLabel(
                document,
                FormatCalendarDayLabel(day.Date, culture),
                (dayLeft + dayRight) / 2,
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
        AllowanceRestoration[] restorations,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyList<DailyUsage> dailyUse,
        LocalTokenUsageSnapshot? tokenUsage,
        long tokenScaleMaximum,
        CultureInfo culture,
        TimeZoneInfo timeZone)
    {
        var period = $"{TimeZoneInfo.ConvertTime(windowStart, timeZone).ToString("ddd d MMM HH:mm", culture)} to {TimeZoneInfo.ConvertTime(windowEnd, timeZone).ToString("ddd d MMM HH:mm", culture)}";
        var daily = FormatDailyTokenAltText(dailyUse, tokenUsage, tokenScaleMaximum, culture);
        var forecastText = forecast switch
        {
            { ReachesLimitBeforeReset: true } => $" Forecast reaches the limit around {TimeZoneInfo.ConvertTime(forecast.EndsAt, timeZone).ToString("ddd d MMM HH:mm", culture)}.",
            { } => $" Forecast leaves {forecast.RemainingPercent:0}% at reset.",
            null => " Forecast is unavailable.",
        };
        var restorationText = restorations.Length > 0
            ? $" {restorations.Length} allowance restoration{(restorations.Length == 1 ? " was" : "s were")} detected; the latest at {TimeZoneInfo.ConvertTime(restorations[^1].DetectedAt, timeZone).ToString("ddd d MMM HH:mm", culture)} increased remaining allowance from {restorations[^1].PreviousRemainingPercent:0}% to {restorations[^1].CurrentRemainingPercent:0}%. Amber markers show detected restorations."
            : " No allowance restorations were detected in this window.";
        return $"Weekly quota trend from {period}. Remaining allowance changed from {first.RemainingPercent:0}% to {last.RemainingPercent:0}% across {sampleCount} observations. The left vertical scale is remaining allowance from 0% to 100%; the independent right scale is locally observed total tokens per calendar day. Horizontal labels are local calendar dates, and reset markers bound the quota window. Solid line connects sampled values across measurement gaps; line breaks mark allowance increases or resets; dashed line is forecast.{restorationText}{forecastText} {daily}";
    }

    private static string FormatDailyTokenAltText(
        IReadOnlyList<DailyUsage> days,
        LocalTokenUsageSnapshot? tokenUsage,
        long tokenScaleMaximum,
        CultureInfo culture)
    {
        if (tokenUsage is null || tokenUsage.Status == LocalTokenUsageStatus.Unavailable)
        {
            return "Local token data is unavailable, so no token bars are shown.";
        }

        var status = tokenUsage.Status == LocalTokenUsageStatus.Partial
            ? "Local token data is partial."
            : "Local token data is complete.";
        var totals = string.Join(
            ", ",
            days.Select(day => $"{day.Date.ToString("ddd d MMM", culture)}: {day.TotalTokens.ToString("N0", culture)}"));
        var scale = tokenScaleMaximum > 0
            ? $" Token bars use a right-axis maximum of {tokenScaleMaximum.ToString("N0", culture)}."
            : string.Empty;
        return $"{status}{scale} Calendar-day token totals are {totals}. The first and last days can be partial, and the current day is measured through now.";
    }

    private static string FormatCompactTokens(double tokens)
    {
        if (tokens >= 1_000_000)
        {
            return $"{(tokens / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture)}M";
        }

        if (tokens >= 1_000)
        {
            return $"{(tokens / 1_000).ToString("0.#", CultureInfo.InvariantCulture)}K";
        }

        return Math.Round(tokens).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatPoints(
        IEnumerable<UsageHistoryEntry> points,
        CalendarDayScale calendarScale) =>
        string.Join(
            " ",
            points.Select(point => $"{Format(calendarScale.GetX(point.RecordedAt))},{Format(GetTrendY(point.RemainingPercent))}"));

    private static double GetTrendY(double remainingPercent) =>
        TrendTop + (100 - Math.Clamp(remainingPercent, 0, 100)) / 100 * TrendHeight;

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private enum BitmapLabelAlignment
    {
        Start,
        Center,
        End,
    }

    private sealed class DailyUsage(
        DateOnly date,
        DateTimeOffset calendarStart,
        DateTimeOffset calendarEnd,
        DateTimeOffset start,
        DateTimeOffset end,
        bool isPartial,
        bool isCurrent)
    {
        public DateOnly Date { get; } = date;

        public DateTimeOffset CalendarStart { get; } = calendarStart;

        public DateTimeOffset CalendarEnd { get; } = calendarEnd;

        public DateTimeOffset Start { get; } = start;

        public DateTimeOffset End { get; } = end;

        public bool IsPartial { get; } = isPartial;

        public bool IsCurrent { get; } = isCurrent;

        public long TotalTokens { get; set; }

        public bool HasTokenData { get; set; }
    }

    private sealed class CalendarDayScale(IReadOnlyList<DailyUsage> days)
    {
        private readonly IReadOnlyList<DailyUsage> _days = days;

        public double GetX(DateTimeOffset instant)
        {
            if (_days.Count == 0 || instant <= _days[0].CalendarStart)
            {
                return Left;
            }

            for (var index = 0; index < _days.Count; index++)
            {
                var day = _days[index];
                if (instant < day.CalendarEnd || index == _days.Count - 1)
                {
                    var duration = day.CalendarEnd - day.CalendarStart;
                    var elapsed = instant - day.CalendarStart;
                    var positionWithinDay = duration > TimeSpan.Zero
                        ? Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1)
                        : 0;
                    return GetDayStartX(index) + positionWithinDay * DayWidth;
                }
            }

            return UsageDashboardCard.BarWidth - Right;
        }

        public double GetDayStartX(int index) => Left + index * DayWidth;

        public double GetDayEndX(int index) => Left + (index + 1) * DayWidth;

        private double DayWidth => _days.Count > 0
            ? (UsageDashboardCard.BarWidth - Left - Right) / _days.Count
            : 0;
    }
}
