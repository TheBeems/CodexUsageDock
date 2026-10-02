using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class WeeklyChartResetTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScheduledResetRetainsBothCyclesAndMarksTheDetectionInterval()
    {
        var previousReset = Now.AddHours(-1);
        var reset = previousReset.AddDays(7);
        UsageHistoryEntry[] history =
        [
            new(previousReset.AddHours(-2), 20, previousReset, 10080),
            new(previousReset.AddMinutes(-5), 10, previousReset, 10080),
            new(previousReset.AddMinutes(5), 100, reset, 10080),
            new(Now, 90, reset, 10080),
        ];

        var chart = Create(history, reset);
        var svg = Parse(chart);
        var lines = ObservedLines(svg);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal(2, Points(line).Length));
        var boundary = Marker(svg, "reset-start");
        Assert.True(PointX(Points(lines[0])[^1]) < X(boundary));
        Assert.True(X(boundary) < PointX(Points(lines[1])[0]));
        var detected = Marker(svg, "quota-reset");
        Assert.Equal(PointX(Points(lines[1])[0]), X(detected));
        Assert.Equal(history[1].RecordedAt.ToString("O", CultureInfo.InvariantCulture), detected.Attribute("data-previous-observed-at")?.Value);
        Assert.Equal(history[2].RecordedAt.ToString("O", CultureInfo.InvariantCulture), detected.Attribute("data-detected-at")?.Value);
        Assert.Contains("between Fri 2 Oct 10:55 and Fri 2 Oct 11:05", detected.Element(Svg + "title")?.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(svg.Descendants(Svg + "line"), line => line.Attribute("data-marker")?.Value == "allowance-restored");
        Assert.Contains("across 4 observations", chart.AltText, StringComparison.Ordinal);
        Assert.Contains("precise event time between observations is unknown", chart.AltText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(95)]
    [InlineData(60)]
    public void ShiftedResetPreservesPriorCycleAndBreaksTheLineEvenWithoutAnIncrease(double remainingAfterChange)
    {
        var currentStart = Now.AddHours(-1);
        var oldReset = currentStart.AddDays(2);
        var reset = currentStart.AddDays(7);
        UsageHistoryEntry[] history =
        [
            new(currentStart.AddHours(-2), 80, oldReset, 10080),
            new(currentStart.AddMinutes(-5), 70, oldReset, 10080),
            new(currentStart.AddMinutes(5), remainingAfterChange, reset, 10080),
            new(Now, remainingAfterChange - 5, reset, 10080),
        ];

        var chart = Create(history, reset);
        var svg = Parse(chart);
        Assert.Equal(2, ObservedLines(svg).Length);
        Assert.NotNull(Marker(svg, "quota-reset"));
        var recent = UsageTrendHistory.LatestSegment(history, currentStart.AddHours(-4), reset, Now, TimeSpan.FromDays(1));
        Assert.Equal(history[2..], recent);
        Assert.Contains("Recorded observations from the preceding seven days are retained across quota resets", chart.AltText, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRolloverKeepsEarlierObservationsAndDoesNotJoinAcrossTheCurrentStart()
    {
        var start = Now.AddHours(-1);
        UsageHistoryEntry[] history =
        [
            new(start.AddHours(-2), 30),
            new(start.AddMinutes(-5), 20),
            new(start.AddMinutes(5), 100),
            new(Now, 90),
        ];

        var svg = Parse(Create(history, start.AddDays(7)));
        Assert.Equal(2, ObservedLines(svg).Length);
        Assert.NotNull(Marker(svg, "quota-reset"));
        Assert.DoesNotContain(svg.Descendants(Svg + "line"), line => line.Attribute("data-marker")?.Value == "allowance-restored");
    }

    [Fact]
    public void SameResetAllowanceIncreaseRetainsTheExistingRestorationMarker()
    {
        var start = Now.AddHours(-1);
        var reset = start.AddDays(7);
        UsageHistoryEntry[] history =
        [
            new(start.AddMinutes(1), 90, reset, 10080),
            new(start.AddMinutes(5), 80, reset, 10080),
            new(start.AddMinutes(10), 95, reset, 10080),
            new(Now, 85, reset, 10080),
        ];

        var chart = Create(history, reset);
        var svg = Parse(chart);
        Assert.Equal(2, ObservedLines(svg).Length);
        Assert.NotNull(Marker(svg, "allowance-restored"));
        Assert.DoesNotContain(svg.Descendants(Svg + "line"), line => line.Attribute("data-marker")?.Value == "quota-reset");
        Assert.Contains("1 allowance restoration was detected", chart.AltText, StringComparison.Ordinal);
    }

    [Fact]
    public void SuppliedHistoricalForecastStartsAtTheSinglePostResetObservation()
    {
        var start = Now.AddMinutes(-5);
        var reset = start.AddDays(7);
        UsageHistoryEntry[] history =
        [
            new(start.AddHours(-1), 30, start, 10080),
            new(start.AddMinutes(-1), 20, start, 10080),
            new(Now, 95, reset, 10080),
        ];

        var svg = Parse(Create(history, reset, new(reset, 45, false)));
        var observed = Assert.Single(ObservedLines(svg));
        Assert.Equal(2, Points(observed).Length);
        var projected = Assert.Single(svg.Descendants(Svg + "polyline"), line => line.Attribute("stroke-dasharray") is not null);
        var latest = Assert.Single(svg.Descendants(Svg + "circle"), circle => circle.Attribute("fill")?.Value == "#5C9EFA"
            && circle.Attribute("r")?.Value == "3" && circle.Attribute("data-marker") is null);
        Assert.Equal(latest.Attribute("cx")?.Value, Points(projected)[0].Split(',')[0]);
        Assert.Equal(latest.Attribute("cy")?.Value, Points(projected)[0].Split(',')[1]);
        Assert.NotNull(Marker(svg, "quota-reset"));
    }

    [Fact]
    public void OneValidObservationStillProducesAChartAndCanAnchorTheSuppliedProjection()
    {
        var reset = Now.AddDays(6);
        var chart = Create([new(Now, 100, reset, 10080)], reset, new(reset, 50, false));
        var svg = Parse(chart);

        Assert.Empty(ObservedLines(svg));
        Assert.Single(svg.Descendants(Svg + "polyline"), line => line.Attribute("stroke-dasharray") is not null);
        Assert.Single(svg.Descendants(Svg + "circle"), circle => circle.Attribute("fill")?.Value == "#5C9EFA");
        Assert.Contains("across 1 observation.", chart.AltText, StringComparison.Ordinal);
    }

    [Fact]
    public void RollingChartExcludesOlderFutureAndInvalidSamplesAndKeepsTheLastDuplicate()
    {
        var reset = Now.AddDays(6);
        UsageHistoryEntry[] history =
        [
            new(Now.AddDays(-7).AddTicks(-1), 100, reset, 10080),
            new(Now.AddDays(-7), 90, reset, 10080),
            new(Now.AddDays(-2), 80, reset, 10080),
            new(Now.AddDays(-1), double.NaN),
            new(Now.AddHours(-4), double.PositiveInfinity),
            new(Now.AddHours(-3), -1),
            new(Now.AddHours(-2), 101),
            null!,
            new(Now, 50, reset, 10080),
            new(Now, 40, reset, 10080),
            new(Now.AddMinutes(1), 30, reset, 10080),
        ];

        var chart = Create(history, reset);
        var svg = Parse(chart);
        Assert.Equal(3, Points(Assert.Single(ObservedLines(svg))).Length);
        Assert.Contains("90% to 40% across 3 observations", chart.AltText, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", chart.ImageUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", chart.ImageUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(59, 1, 4)]
    [InlineData(61, 2, 2)]
    public void ResetScheduleJitterDoesNotCreateAnArtificialCycleBreak(int seconds, int expectedLines, int expectedRecentSamples)
    {
        var reset = Now.AddDays(6);
        var shiftedReset = reset.AddSeconds(seconds);
        UsageHistoryEntry[] history =
        [
            new(Now.AddMinutes(-30), 80, reset, 10080),
            new(Now.AddMinutes(-20), 75, reset, 10080),
            new(Now.AddMinutes(-10), 70, shiftedReset, 10080),
            new(Now, 65, shiftedReset, 10080),
        ];

        var svg = Parse(Create(history, shiftedReset));
        Assert.Equal(expectedLines, ObservedLines(svg).Length);
        Assert.Equal(expectedRecentSamples, UsageTrendHistory.LatestSegment(history, shiftedReset.AddDays(-7), shiftedReset,
            Now, TimeSpan.FromHours(1)).Length);
        Assert.Equal(seconds > 60, svg.Descendants(Svg + "line").Any(line => line.Attribute("data-marker")?.Value == "quota-reset"));
    }

    [Fact]
    public void RetainedCyclesShareThePointBudgetAndKeepTheLatestReading()
    {
        var currentStart = Now.AddHours(-1);
        var reset = currentStart.AddDays(7);
        var samples = Enumerable.Range(0, 10081).Select(index =>
        {
            var at = Now.AddDays(-7).AddMinutes(index);
            var afterReset = at >= currentStart;
            return new UsageHistoryEntry(at, afterReset ? 100 - (at - currentStart).TotalMinutes / 2 : 80 - index / 200d,
                afterReset ? reset : currentStart, 10080);
        }).ToArray();

        var chart = Create(samples, reset);
        var svg = Parse(chart);
        var lines = ObservedLines(svg);
        Assert.Equal(2, lines.Length);
        Assert.InRange(lines.Sum(line => Points(line).Length), 4, WeeklyUsageTrendChartRenderer.MaximumRenderedPoints);
        var latestLabel = Assert.Single(svg.Descendants(Svg + "g"), label => label.Attribute("data-axis")?.Value == "current");
        Assert.Equal("70%", latestLabel.Attribute("data-axis-label")?.Value);
        Assert.Equal(X(Marker(svg, "now")), PointX(Points(lines[^1])[^1]));
    }

    private static WeeklyUsageTrendChart Create(UsageHistoryEntry[] history, DateTimeOffset reset, UsageTrendForecast? forecast = null) =>
        Assert.IsType<WeeklyUsageTrendChart>(WeeklyUsageTrendChartRenderer.Create(history,
            new RateLimitWindow(10, 10080, reset), Now, TimeSpan.FromMinutes(15), forecast,
            culture: CultureInfo.InvariantCulture, timeZone: TimeZoneInfo.Utc));

    private static XDocument Parse(WeeklyUsageTrendChart chart) =>
        XDocument.Parse(Uri.UnescapeDataString(chart.ImageUrl["data:image/svg+xml;utf8,".Length..]));

    private static XElement[] ObservedLines(XDocument svg) =>
        svg.Descendants(Svg + "polyline").Where(line => line.Attribute("stroke-dasharray") is null).ToArray();

    private static XElement Marker(XDocument svg, string name) =>
        Assert.Single(svg.Descendants(Svg + "line"), line => line.Attribute("data-marker")?.Value == name);

    private static string[] Points(XElement line) => line.Attribute("points")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static double PointX(string point) => double.Parse(point.Split(',')[0], CultureInfo.InvariantCulture);

    private static double X(XElement marker) => double.Parse(marker.Attribute("x1")!.Value, CultureInfo.InvariantCulture);
}
