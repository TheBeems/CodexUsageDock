using System.Globalization;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace CodexUsageDock.Tests;

public sealed class WeeklyChartReadabilityTests(ITestOutputHelper output)
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MovingUnusedResetEstimatesKeepOneObservedLineWithoutResetMarkers()
    {
        var idle = Enumerable.Range(0, 200).Select(index =>
        {
            var at = Now.AddHours(-10).AddMinutes(index * 3);
            return new UsageHistoryEntry(at, 100, at.AddDays(7), 10080);
        }).ToArray();
        var reset = idle[^1].ResetsAt!.Value;
        var history = idle.Append(new UsageHistoryEntry(Now, 95, reset, 10080)).ToArray();
        var chart = Create(history, reset);
        var svg = Parse(chart);

        Assert.Single(ObservedLines(svg));
        Assert.Empty(ResetMarkers(svg));
        Assert.Contains("No quota reset or reset-schedule changes", chart.AltText, StringComparison.Ordinal);
    }

    [Fact]
    public void UnusedEstimateCanSettleBetweenObservationsWhenConsumptionStarts()
    {
        var previousAt = Now.AddMinutes(-5);
        var startedAt = Now.AddMinutes(-2);
        UsageHistoryEntry previous = new(previousAt, 100, previousAt.AddDays(7), 10080);
        UsageHistoryEntry current = new(Now, 99, startedAt.AddDays(7), 10080);

        Assert.False(WeeklyAllowanceRestoration.IsCycleChange(previous, current));
        Assert.Empty(ResetMarkers(Parse(Create([previous, current], current.ResetsAt!.Value))));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(80)]
    public void FixedCycleChangesArePreservedEvenWithoutAnAllowanceIncrease(double remaining)
    {
        UsageHistoryEntry previous = new(Now.AddMinutes(-5), remaining, Now.AddDays(3), 10080);
        UsageHistoryEntry current = new(Now, remaining, Now.AddDays(6), 10080);
        Assert.True(WeeklyAllowanceRestoration.IsCycleChange(previous, current));
    }

    [Fact]
    public void ScheduledRolloverAtFullAllowanceIsStillACycleChange()
    {
        UsageHistoryEntry previous = new(Now.AddMinutes(-5), 100, Now.AddMinutes(-1), 10080);
        UsageHistoryEntry current = new(Now, 100, Now.AddDays(7), 10080);
        Assert.True(WeeklyAllowanceRestoration.IsCycleChange(previous, current));
    }

    [Fact]
    public void MissingWindowMetadataCannotBeAssumedToBeAnUnusedEstimate()
    {
        var previousAt = Now.AddMinutes(-5);
        UsageHistoryEntry previous = new(previousAt, 100, previousAt.AddDays(7));
        UsageHistoryEntry current = new(Now, 100, Now.AddDays(7));
        Assert.True(WeeklyAllowanceRestoration.IsCycleChange(previous, current));
    }

    [Fact]
    public void RetainedHistoryShowsOnlyRealResetEventsAndNoDuplicateRestorationLines()
    {
        var chart = CreateScenario();
        var svg = Parse(chart);
        output.WriteLine("CHART_PREVIEW_SVG=" + svg.ToString(SaveOptions.DisableFormatting));

        Assert.Equal(2, ResetMarkers(svg).Length);
        Assert.Equal(2, svg.Descendants(Svg + "g").Count(group => group.Attribute("data-axis")?.Value == "reset-detected"));
        Assert.DoesNotContain(svg.Descendants(Svg + "line"), line => line.Attribute("data-marker")?.Value == "allowance-restored");
        Assert.Equal("56%", Assert.Single(svg.Descendants(Svg + "g"), group => group.Attribute("data-axis")?.Value == "current")
            .Attribute("data-axis-label")?.Value);
        Assert.Contains("across quota resets", chart.AltText, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetAndNowLabelsFitInsideTheHeaderWithoutOverlapping()
    {
        var svg = Parse(CreateScenario());
        var labels = svg.Descendants(Svg + "g")
            .Where(group => group.Attribute("data-axis")?.Value is "marker" or "reset-detected")
            .Select(LabelBounds).ToArray();
        Assert.Contains(svg.Descendants(Svg + "g"), group => group.Attribute("data-axis-label")?.Value == "Now");
        Assert.Contains(svg.Descendants(Svg + "g"), group => group.Attribute("data-axis-label")?.Value == "Next reset");
        Assert.DoesNotContain(svg.Descendants(Svg + "g"), group => group.Attribute("data-axis-label")?.Value == "START");
        Assert.All(labels, bounds => Assert.InRange(bounds.Top + bounds.Height, 0, PlotTop(svg)));
        AssertNonOverlapping([.. labels, .. LegendLabels(svg).Select(LabelBounds)]);
    }

    [Fact]
    public void DenseCalendarLabelsKeepEveryDateWithoutOverlapping()
    {
        var svg = Parse(CreateScenario());
        var dayLabels = svg.Descendants(Svg + "g").Where(group => group.Attribute("data-axis")?.Value == "horizontal").ToArray();
        Assert.Equal(14, dayLabels.Length);
        Assert.Equal("Sat 26", dayLabels[0].Attribute("data-axis-label")?.Value);
        Assert.Equal("Fri 9", dayLabels[^1].Attribute("data-axis-label")?.Value);
        Assert.All(dayLabels, group => Assert.Equal(2, group.Elements(Svg + "g").Count()));
        var visibleLabels = dayLabels.SelectMany(group => group.Elements(Svg + "path").Any()
            ? new[] { group } : group.Elements(Svg + "g")).Select(LabelBounds).ToArray();
        Assert.All(visibleLabels, bounds =>
        {
            Assert.InRange(bounds.Left, 0, WeeklyUsageTrendChartRenderer.Width - bounds.Width);
            Assert.InRange(bounds.Top + bounds.Height, 0, WeeklyUsageTrendChartRenderer.Height - 4);
        });
        AssertNonOverlapping(visibleLabels);
    }

    [Fact]
    public void LegendExplainsObservedForecastAndIndependentTokenSeries()
    {
        var svg = Parse(CreateScenario());
        Assert.Equal(["Quota remaining (%)", "Forecast", "Local tokens/day (right axis)"],
            LegendLabels(svg).Select(group => group.Attribute("data-axis-label")!.Value));
        var observed = Assert.Single(svg.Descendants(Svg + "line"), line => line.Attribute("data-legend-series")?.Value == "remaining");
        Assert.Null(observed.Attribute("stroke-dasharray"));
        var forecast = Assert.Single(svg.Descendants(Svg + "line"), line => line.Attribute("data-legend-series")?.Value == "forecast");
        Assert.Equal("5 4", forecast.Attribute("stroke-dasharray")?.Value);
        Assert.Single(svg.Descendants(Svg + "rect"), rect => rect.Attribute("data-legend-series")?.Value == "daily-tokens");
        AssertNonOverlapping(LegendLabels(svg).Select(LabelBounds).ToArray());
    }

    [Fact]
    public void EveryScenarioLabelRendersItsPunctuationWithoutReplacementCharacters()
    {
        var svg = Parse(CreateScenario());
        Assert.All(svg.Descendants(Svg + "g").Where(group => group.Attribute("data-rendered-label") is not null),
            group => Assert.DoesNotContain("?", group.Attribute("data-rendered-label")!.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void MissingForecastAndTokenDataDoNotAdvertiseAbsentSeries()
    {
        var reset = Now.AddDays(6);
        var svg = Parse(Create([new(Now.AddHours(-1), 80, reset, 10080), new(Now, 75, reset, 10080)], reset));
        Assert.Equal("Quota remaining (%)", Assert.Single(LegendLabels(svg)).Attribute("data-axis-label")?.Value);
        Assert.DoesNotContain(svg.Descendants(), element => element.Attribute("data-legend-series")?.Value is "forecast" or "daily-tokens");
        Assert.DoesNotContain(svg.Descendants(Svg + "g"), group => group.Attribute("data-axis")?.Value == "tokens");
    }

    [Fact]
    public void FutureAreaBeginsAtNowAndKeepsTheNextResetVisible()
    {
        var svg = Parse(CreateScenario());
        var now = Assert.Single(svg.Descendants(Svg + "line"), line => line.Attribute("data-marker")?.Value == "now");
        var future = Assert.Single(svg.Descendants(Svg + "rect"), rect => rect.Attribute("data-area")?.Value == "future");
        Assert.Equal(now.Attribute("x1")?.Value, future.Attribute("x")?.Value);
        Assert.Equal(now.Attribute("y1")?.Value, future.Attribute("y")?.Value);
        Assert.Contains(svg.Descendants(Svg + "g"), group => group.Attribute("data-axis-label")?.Value == "Next reset");
        Assert.All(ResetMarkers(svg), reset => Assert.Equal("#F2C94C", reset.Attribute("stroke")?.Value));
    }

    [Fact]
    public void BillionTokenScaleUsesACompactLabelAtTheTopGridline()
    {
        var svg = Parse(CreateScenario());
        var topTokenLabel = Assert.Single(svg.Descendants(Svg + "g"), group => group.Attribute("data-axis")?.Value == "tokens"
            && group.Attribute("data-axis-label")?.Value == "1B");
        Assert.Equal(PlotTop(svg) - ChartLabelGlyphs.Height / 2, LabelBounds(topTokenLabel).Top);
        var zeroTokenLabel = Assert.Single(svg.Descendants(Svg + "g"), group => group.Attribute("data-axis")?.Value == "tokens"
            && group.Attribute("data-axis-label")?.Value == "0");
        var zeroGridline = Assert.Single(svg.Descendants(Svg + "line"), line => line.Attribute("data-grid")?.Value == "remaining-percent"
            && line.Attribute("data-value")?.Value == "0");
        Assert.Equal(double.Parse(zeroGridline.Attribute("y1")!.Value, CultureInfo.InvariantCulture) - ChartLabelGlyphs.Height / 2,
            LabelBounds(zeroTokenLabel).Top);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5.9)]
    public void EstimatedLimitPointKeepsItsDateWithoutARepeatedLabelCoveringThePlot(double daysAhead)
    {
        var reset = Now.AddDays(6);
        var svg = Parse(Create([new(Now.AddHours(-1), 67, reset, 10080), new(Now, 66, reset, 10080)],
            reset, new(Now.AddDays(daysAhead), 0, true)));
        Assert.DoesNotContain(svg.Descendants(Svg + "g"), group => group.Attribute("data-axis")?.Value == "estimated-limit");
        var point = Assert.Single(svg.Descendants(Svg + "circle"), circle => circle.Attribute("data-marker")?.Value == "estimated-limit");
        var forecast = Assert.Single(svg.Descendants(Svg + "polyline"), line => line.Attribute("stroke-dasharray") is not null);
        var endpoint = forecast.Attribute("points")!.Value.Split(' ')[^1].Split(',');
        Assert.Equal(endpoint[0], point.Attribute("cx")!.Value);
        Assert.Equal(endpoint[1], point.Attribute("cy")!.Value);
        Assert.Contains("Estimated limit:", point.Element(Svg + "title")?.Value, StringComparison.Ordinal);
        Assert.Contains("actual usage may differ", point.Element(Svg + "title")?.Value, StringComparison.Ordinal);
    }

    private static WeeklyUsageTrendChart CreateScenario()
    {
        var firstReset = new DateTimeOffset(2026, 9, 26, 16, 0, 0, TimeSpan.Zero);
        var currentStart = new DateTimeOffset(2026, 10, 2, 21, 10, 0, TimeSpan.Zero);
        var idle = Enumerable.Range(0, 124).Select(index =>
        {
            var at = firstReset.AddMinutes(index * 20);
            return new UsageHistoryEntry(at, 100, at.AddDays(7), 10080);
        }).ToArray();
        var oldReset = idle[^1].ResetsAt!.Value;
        UsageHistoryEntry[] history =
        [
            new(firstReset.AddHours(-2), 1, firstReset.AddHours(3), 10080),
            .. idle,
            new(idle[^1].RecordedAt.AddMinutes(10), 95, oldReset, 10080),
            new(currentStart.AddDays(-2), 70, oldReset, 10080),
            new(currentStart.AddDays(-1), 60, oldReset, 10080),
            new(currentStart.AddMinutes(-15), 36, oldReset, 10080),
            new(currentStart, 100, currentStart.AddDays(7), 10080),
            new(Now, 56, currentStart.AddDays(7), 10080),
        ];
        var tokens = new LocalTokenUsageSnapshot(
        [
            new(new DateOnly(2026, 9, 26), 850_000_000),
            new(new DateOnly(2026, 9, 27), 350_000_000),
            new(new DateOnly(2026, 10, 1), 250_000_000),
            new(new DateOnly(2026, 10, 2), 600_000_000),
            new(new DateOnly(2026, 10, 3), 200_000_000),
        ], Now, LocalTokenUsageStatus.Complete);
        return Create(history, currentStart.AddDays(7), new(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), 0, true), tokens);
    }

    private static WeeklyUsageTrendChart Create(UsageHistoryEntry[] history, DateTimeOffset reset,
        UsageTrendForecast? forecast = null, LocalTokenUsageSnapshot? tokens = null) =>
        Assert.IsType<WeeklyUsageTrendChart>(WeeklyUsageTrendChartRenderer.Create(history,
            new RateLimitWindow(44, 10080, reset), Now, TimeSpan.FromMinutes(15), forecast, tokens,
            CultureInfo.InvariantCulture, TimeZoneInfo.Utc));

    private static XDocument Parse(WeeklyUsageTrendChart chart) =>
        XDocument.Parse(Uri.UnescapeDataString(chart.ImageUrl["data:image/svg+xml;utf8,".Length..]));

    private static XElement[] ObservedLines(XDocument svg) => svg.Descendants(Svg + "polyline")
        .Where(line => line.Attribute("stroke-dasharray") is null).ToArray();

    private static XElement[] ResetMarkers(XDocument svg) => svg.Descendants(Svg + "line")
        .Where(line => line.Attribute("data-marker")?.Value == "quota-reset").ToArray();

    private static XElement[] LegendLabels(XDocument svg) => svg.Descendants(Svg + "g")
        .Where(group => group.Attribute("data-axis")?.Value == "legend").ToArray();

    private static double PlotTop(XDocument svg) => double.Parse(svg.Descendants(Svg + "line")
        .Single(line => line.Attribute("data-grid")?.Value == "remaining-percent" && line.Attribute("data-value")?.Value == "100")
        .Attribute("y1")!.Value, CultureInfo.InvariantCulture);

    private static (double Left, double Top, double Width, double Height) LabelBounds(XElement group)
    {
        var translate = group.Descendants(Svg + "path").First().Attribute("transform")!.Value["translate(".Length..^1].Split(' ');
        var rendered = group.Attribute("data-rendered-label")!.Value;
        return (double.Parse(translate[0], CultureInfo.InvariantCulture), double.Parse(translate[1], CultureInfo.InvariantCulture),
            rendered.Sum(character => ChartLabelGlyphs.Values[character].Width), ChartLabelGlyphs.Height);
    }

    private static void AssertNonOverlapping((double Left, double Top, double Width, double Height)[] labels)
    {
        for (var first = 0; first < labels.Length; first++)
        {
            for (var second = first + 1; second < labels.Length; second++)
            {
                var a = labels[first];
                var b = labels[second];
                Assert.False(a.Left < b.Left + b.Width && a.Left + a.Width > b.Left
                    && a.Top < b.Top + b.Height && a.Top + a.Height > b.Top,
                    $"Labels overlap: {a} and {b}.");
            }
        }
    }
}
