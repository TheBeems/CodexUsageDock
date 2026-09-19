using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class UsageDashboardTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    private static CodexUsageSnapshot Snapshot() => CodexUsageSnapshot.Loading with
    {
        Primary = new RateLimitWindow(25, 300, Now.AddHours(4)),
        Secondary = new RateLimitWindow(33, 10080, Now.AddDays(4)),
        UpdatedAt = Now,
        Source = UsageDataSource.AppServer,
        Error = null,
        DefaultBucketId = "codex",
    };

    private static JsonDocument Render(CodexUsageSnapshot snapshot, UsageHistoryEntry[]? history = null) =>
        JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(snapshot, Now, false, [],
            history ?? [], TimeSpan.FromMinutes(1),
            tokenUsage: new LocalTokenUsageSnapshot([new DailyTokenUsage(new DateOnly(2026, 9, 15), 1000)],
                Now, LocalTokenUsageStatus.Complete)));

    private static XDocument Chart(JsonElement data) => XDocument.Parse(
        Uri.UnescapeDataString(data.GetProperty("weeklyTrendChartUrl").GetString()!["data:image/svg+xml;utf8,".Length..]));

    [Fact]
    public void CodexAndSparkShareRemainingAndTimeBarsWithoutCombiningCategories()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Buckets =
            [
                new RateLimitBucket("codex", "Codex", snapshot.Primary, snapshot.Secondary),
                new RateLimitBucket("spark", "Codex Spark",
                    new RateLimitWindow(25, 300, Now.AddHours(4)),
                    new RateLimitWindow(80, 10080, Now.AddDays(6))),
            ],
        };
        using var data = Render(snapshot);
        var groups = data.RootElement.GetProperty("quotaGroups");
        Assert.Equal(2, groups.GetArrayLength());
        Assert.Equal("Codex Spark", groups[1].GetProperty("name").GetString());
        var codex = groups[0].GetProperty("windows");
        var spark = groups[1].GetProperty("windows");
        Assert.Equal(2, codex.GetArrayLength());
        Assert.Equal(2, spark.GetArrayLength());
        Assert.Equal("75%", codex[0].GetProperty("remainingPercent").GetString());
        Assert.Equal("20%", spark[0].GetProperty("elapsedPercent").GetString());
        Assert.Equal(codex[0].GetProperty("remainingBarUrl").GetString(), spark[0].GetProperty("remainingBarUrl").GetString());
        Assert.Equal(codex[0].GetProperty("elapsedBarUrl").GetString(), spark[0].GetProperty("elapsedBarUrl").GetString());
        Assert.Equal("67%", codex[1].GetProperty("remainingPercent").GetString());
        Assert.Equal("20%", spark[1].GetProperty("remainingPercent").GetString());
        Assert.Contains("75% remaining", codex[0].GetProperty("remainingBarAlt").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void ArbitraryDefaultDurationsAppearOnceWithTheirActualDuration()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Buckets = [new RateLimitBucket("codex", "Codex",
                new RateLimitWindow(10, 90, Now.AddMinutes(45)), snapshot.Secondary)],
        };
        using var data = Render(snapshot);
        var groups = data.RootElement.GetProperty("quotaGroups");
        Assert.Equal(1, groups.GetArrayLength());
        var windows = groups[0].GetProperty("windows");
        Assert.Equal(3, windows.GetArrayLength());
        Assert.Equal("90-minute", windows[2].GetProperty("title").GetString());
        Assert.Equal("50%", windows[2].GetProperty("elapsedPercent").GetString());
    }

    [Fact]
    public void WeeklyPrimaryDoesNotInventAnUnreportedWeeklyWindow()
    {
        using var data = Render(Snapshot() with
        {
            Buckets = [new RateLimitBucket("spark", "Spark",
                new RateLimitWindow(25, 10080, Now.AddDays(6)), null)],
        });
        var spark = data.RootElement.GetProperty("quotaGroups")[1];
        Assert.False(spark.GetProperty("hasInactiveWindows").GetBoolean());
        var window = Assert.Single(spark.GetProperty("windows").EnumerateArray());
        Assert.Equal("Weekly", window.GetProperty("title").GetString());
        Assert.Equal("14%", window.GetProperty("elapsedPercent").GetString());
    }

    [Fact]
    public void MissingExpiredAndReportedZeroRemainDistinct()
    {
        using var data = Render(Snapshot() with
        {
            Primary = null,
            Secondary = new RateLimitWindow(0, 10080, Now.AddDays(7)),
            Buckets = [new RateLimitBucket("spark", "Spark",
                new RateLimitWindow(0, 300, Now.AddMinutes(-1)), null)],
        });
        var groups = data.RootElement.GetProperty("quotaGroups");
        Assert.Contains("Not reported", groups[0].GetProperty("inactiveWindows").GetString(), StringComparison.Ordinal);
        var zero = Assert.Single(groups[0].GetProperty("windows").EnumerateArray());
        Assert.Equal("100%", zero.GetProperty("remainingPercent").GetString());
        Assert.Equal("0%", zero.GetProperty("elapsedPercent").GetString());
        Assert.Empty(groups[1].GetProperty("windows").EnumerateArray());
        Assert.Contains("Awaiting refresh", groups[1].GetProperty("inactiveWindows").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN, 300)]
    [InlineData(double.PositiveInfinity, 300)]
    [InlineData(101, 300)]
    [InlineData(50, 0)]
    [InlineData(50, int.MaxValue)]
    public void MalformedWindowDoesNotProduceAZeroBarOrThrow(double used, int minutes)
    {
        using var data = Render(Snapshot() with { Primary = new RateLimitWindow(used, minutes, Now.AddHours(4)) });
        Assert.Single(data.RootElement.GetProperty("quotaGroups")[0].GetProperty("windows").EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OldObservationsCannotLookLikeCurrentTimeComparisons(bool lastConfirmed)
    {
        using var data = Render(Snapshot() with
        {
            Source = lastConfirmed ? UsageDataSource.LastConfirmed : UsageDataSource.AppServer,
            UpdatedAt = Now.AddHours(-1),
        });
        Assert.True(data.RootElement.GetProperty("hasNotice").GetBoolean());
        foreach (var window in data.RootElement.GetProperty("quotaGroups")[0].GetProperty("windows").EnumerateArray())
        {
            Assert.Equal("—", window.GetProperty("elapsedPercent").GetString());
            Assert.False(window.GetProperty("elapsedAvailable").GetBoolean());
            Assert.Equal(string.Empty, window.GetProperty("elapsedBarUrl").GetString());
            Assert.Contains("last observation", window.GetProperty("remainingBarAlt").GetString(), StringComparison.Ordinal);
        }
        Assert.Contains("paused", data.RootElement.GetProperty("weeklyForecastSummary").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OldHistoryDoesNotInflateTheContinuousForecastProgress()
    {
        using var data = Render(Snapshot(), history:
        [
            new(Now.AddDays(-2), 100), new(Now.AddDays(-2).AddMinutes(5), 90),
            new(Now.AddMinutes(-19), 75), new(Now.AddMinutes(-10), 70), new(Now, 67),
        ]);
        Assert.Equal("Forecast · 19/30 min of continuous data",
            data.RootElement.GetProperty("weeklyForecastSummary").GetString());
        Assert.DoesNotContain("On track", data.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultChartFallsWithUsageAndIncludesTokensOnTheRightAxis()
    {
        UsageHistoryEntry[] history = [new(Now.AddMinutes(-5), 80), new(Now, 67)];
        using var compact = Render(Snapshot(), history: history);
        var chart = Chart(compact.RootElement);
        Assert.Single(chart.Descendants(Svg + "rect"), node => node.Attribute("data-series")?.Value == "daily-tokens");
        Assert.Contains(chart.Descendants(Svg + "g"), node => node.Attribute("data-axis")?.Value == "tokens");
        var line = Assert.Single(chart.Descendants(Svg + "polyline"));
        var points = line.Attribute("points")!.Value.Split(' ');
        var firstY = double.Parse(points[0].Split(',')[1], CultureInfo.InvariantCulture);
        var lastY = double.Parse(points[^1].Split(',')[1], CultureInfo.InvariantCulture);
        Assert.True(lastY > firstY, "Increasing usage must move down the remaining-quota axis.");
        Assert.Contains("80% to 67%", compact.RootElement.GetProperty("weeklyTrendChartAlt").GetString(), StringComparison.Ordinal);
        Assert.Contains(chart.Descendants(Svg + "g"), node => node.Attribute("data-axis-label")?.Value == "67%");
        Assert.NotEmpty(chart.Descendants(Svg + "path"));
    }

    [Fact]
    public void InformationIsAvailableThroughARegularCommandWithoutAFormAction()
    {
        using var service = _environment.CreateService();
        using var page = new CodexUsageDockPage(service, _environment.CreateSettings());
        var form = Assert.IsAssignableFrom<FormContent>(Assert.Single(page.GetContent()));
        Assert.Null(page.Details);
        var command = Assert.Single(page.Commands.OfType<CommandContextItem>(), item => item.Title == "Usage information");
        var information = Assert.IsType<UsageInformationPage>(command.Command);
        Assert.Contains("Forecast and budget", Assert.IsType<MarkdownContent>(Assert.Single(information.GetContent())).Body, StringComparison.Ordinal);
        page.Refresh();
        Assert.DoesNotContain("Action.Submit", form.TemplateJson, StringComparison.Ordinal);
        Assert.Contains("Chart guide", Assert.IsType<MarkdownContent>(Assert.Single(information.GetContent())).Body, StringComparison.Ordinal);
        Assert.Null(page.Details);
    }

    [Theory]
    [InlineData(0, "100%", 534)]
    [InlineData(43, "57%", 304.38)]
    [InlineData(100, "0%", 0)]
    public void RemainingBarDrainsWhileElapsedTimeFills(double used, string remaining, double width)
    {
        using var data = Render(Snapshot() with { Primary = new(used, 300, Now.AddHours(4)) });
        var window = data.RootElement.GetProperty("quotaGroups")[0].GetProperty("windows")[0];
        Assert.Equal(remaining, window.GetProperty("remainingPercent").GetString());
        Assert.Equal("20%", window.GetProperty("elapsedPercent").GetString());
        var bar = XDocument.Parse(Uri.UnescapeDataString(window.GetProperty("remainingBarUrl").GetString()!["data:image/svg+xml;utf8,".Length..]));
        var fill = bar.Descendants(Svg + "rect").SingleOrDefault(rect => rect.Attribute("fill")?.Value == "#5C9EFA");
        if (width == 0) Assert.Null(fill);
        else Assert.Equal(width, double.Parse(Assert.IsType<XElement>(fill).Attribute("width")!.Value, CultureInfo.InvariantCulture), precision: 2);
    }

    [Theory]
    [InlineData(0, "15:00")]
    [InlineData(3, "15:15")]
    [InlineData(59, "16:00")]
    public void ForecastStopsAtTheLimitWithoutExtendingToResetOrInventingEarlierMeasurements(int minutes, string expectedLabel)
    {
        var firstAt = Now.AddHours(-1);
        var limitAt = Now.AddHours(3).AddMinutes(minutes);
        var reset = Now.AddDays(4);
        var chart = WeeklyUsageTrendChartRenderer.Create([new(firstAt, 70), new(Now, 57)],
            new(43, 10080, reset), Now, TimeSpan.FromHours(2), new(limitAt, 0, true),
            culture: CultureInfo.InvariantCulture, timeZone: TimeZoneInfo.Utc)!;
        var svg = XDocument.Parse(Uri.UnescapeDataString(chart.ImageUrl["data:image/svg+xml;utf8,".Length..]));
        var observed = Assert.Single(svg.Descendants(Svg + "polyline"), line => line.Attribute("stroke-dasharray") is null);
        Assert.Equal(2, observed.Attribute("points")!.Value.Split(' ').Length);
        var forecast = Assert.Single(svg.Descendants(Svg + "polyline"), line => line.Attribute("stroke-dasharray") is not null);
        var points = forecast.Attribute("points")!.Value.Split(' ');
        Assert.Equal(2, points.Length);
        var endpoint = points[^1].Split(',');
        var resetMarker = Assert.Single(svg.Descendants(Svg + "line"), line => line.Attribute("data-marker")?.Value == "reset-end");
        Assert.True(double.Parse(endpoint[0], CultureInfo.InvariantCulture) < double.Parse(resetMarker.Attribute("x1")!.Value, CultureInfo.InvariantCulture));
        var zero = Assert.Single(svg.Descendants(Svg + "line"), line => line.Attribute("data-grid")?.Value == "remaining-percent" && line.Attribute("data-value")?.Value == "0");
        Assert.Equal(zero.Attribute("y1")!.Value, endpoint[1]);
        Assert.Contains(svg.Descendants(Svg + "g"), label => label.Attribute("data-axis")?.Value == "estimated-limit"
            && label.Attribute("data-axis-label")?.Value == expectedLabel);
        Assert.Contains($"Estimated limit: {expectedLabel}", chart.AltText, StringComparison.Ordinal);
        // Label rounding must not move the actual plotted limit point.
        var marker = Assert.Single(svg.Descendants(Svg + "circle"), point => point.Attribute("data-marker")?.Value == "estimated-limit");
        Assert.Equal(endpoint[0], marker.Attribute("cx")!.Value);
    }

    [Fact]
    public void WeeklyRiskAppearsAtTheTopAndUnusedWindowsKeepConsistentHeadings()
    {
        var history = Enumerable.Range(0, 7).Select(index => new UsageHistoryEntry(Now.AddMinutes(-30 + index * 5), 90 - index * 5)).ToArray();
        using var data = Render(Snapshot() with { Primary = null, Secondary = new(40, 10080, Now.AddDays(4)) }, history);
        Assert.True(data.RootElement.GetProperty("hasWeeklyForecast").GetBoolean());
        Assert.Equal("Warning", data.RootElement.GetProperty("forecastColor").GetString());
        var codex = data.RootElement.GetProperty("quotaGroups")[0];
        Assert.Equal("Codex", codex.GetProperty("name").GetString());
        Assert.Equal("Codex · Weekly", codex.GetProperty("heading").GetString());
        Assert.True(codex.GetProperty("isPrimary").GetBoolean());
        Assert.Equal("Weekly", Assert.Single(codex.GetProperty("windows").EnumerateArray()).GetProperty("title").GetString());
        Assert.Contains("5-hour", codex.GetProperty("inactiveWindows").GetString(), StringComparison.Ordinal);
        Assert.True(UsageDashboardCard.TemplateJson.IndexOf("weeklyForecastSummary", StringComparison.Ordinal)
            < UsageDashboardCard.TemplateJson.IndexOf("quotaGroups", StringComparison.Ordinal));
    }

    [Fact]
    public void SingleAndPairedWindowsKeepTheSameBarThicknessAtTheirRespectiveWidths()
    {
        using var single = Render(Snapshot() with { Primary = null });
        using var paired = Render(Snapshot());
        var singleWindow = single.RootElement.GetProperty("quotaGroups")[0].GetProperty("windows")[0];
        var pairedWindow = paired.RootElement.GetProperty("quotaGroups")[0].GetProperty("windows")[1];
        Assert.False(singleWindow.GetProperty("showTitle").GetBoolean());
        Assert.True(pairedWindow.GetProperty("showTitle").GetBoolean());
        foreach (var key in new[] { "remainingBarUrl", "elapsedBarUrl" })
        {
            static XElement SvgRoot(string url) => XDocument.Parse(Uri.UnescapeDataString(url["data:image/svg+xml;utf8,".Length..])).Root!;
            var full = SvgRoot(singleWindow.GetProperty(key).GetString()!);
            var half = SvgRoot(pairedWindow.GetProperty(key).GetString()!);
            Assert.Equal((double)half.Attribute("width")! * 2, (double)full.Attribute("width")!);
            Assert.Equal((double)half.Attribute("height")!, (double)full.Attribute("height")!);
        }
    }

    [Fact]
    public void InformationOmitsMissingForecastSectionsAndRepeatedQuotaRows()
    {
        using var data = Render(Snapshot() with { Primary = null });
        var details = CodexUsageDockPage.FormatForecastDetails(data.RootElement);
        Assert.DoesNotContain("### 5-hour", details, StringComparison.Ordinal);
        Assert.Contains("### Weekly", details, StringComparison.Ordinal);
        Assert.Contains("independent right axis", details, StringComparison.Ordinal);
        Assert.Contains("conditional estimate", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Other quota windows", CodexUsageDockPage.FormatDetailsBody(Snapshot(), Now), StringComparison.Ordinal);
        using var empty = Render(Snapshot() with { Primary = null, Secondary = null });
        Assert.Contains("No active quota windows reported", CodexUsageDockPage.FormatForecastDetails(empty.RootElement), StringComparison.Ordinal);
    }

    [Fact]
    public void InformationDatesUseTheSameEnglishLocalFormatAsTheDashboard()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
            var expiry = Now.AddDays(19);
            var details = CodexUsageDockPage.FormatResetCredits(new(1, [new("Full reset", "available", expiry)]));
            Assert.Contains(expiry.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture), details, StringComparison.Ordinal);
            Assert.Equal(UsageTrendAnalyzer.FormatWeeklyLimitEstimate(expiry, Now, CultureInfo.InvariantCulture),
                UsageTrendAnalyzer.FormatWeeklyLimitEstimate(expiry, Now));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("used", false)]
    [InlineData("expired", false)]
    [InlineData("unknown", false)]
    [InlineData("available", true)]
    [InlineData("AVAILABLE", true)]
    [InlineData(" available ", true)]
    [InlineData(null, true)]
    public void ResetExpiryEmphasisOnlyUsesAvailableCredits(string? status, bool urgent)
    {
        using var data = Render(Snapshot() with
        {
            ResetCredits = new(1,
            [
                new("Full reset", status, Now.AddHours(2)),
                new("Full reset", "available", Now.AddDays(19)),
            ]),
        });
        var summary = data.RootElement.GetProperty("resetCreditsSummary").GetString()!;
        Assert.Equal(urgent, summary.Contains("expires", StringComparison.Ordinal));
        Assert.Equal(urgent ? "Warning" : "Default", data.RootElement.GetProperty("resetCreditsColor").GetString());
    }

    [Theory]
    [InlineData(19, "Default")]
    [InlineData(0.5, "Warning")]
    public void OnlyAnUpcomingResetCreditExpiryNeedsEmphasis(double days, string color)
    {
        using var data = Render(Snapshot() with
        {
            ResetCredits = new(2, [new("Full reset", "available", Now.AddDays(days))]),
        });
        Assert.True(data.RootElement.GetProperty("hasResetCredits").GetBoolean());
        Assert.StartsWith("2 reset credits", data.RootElement.GetProperty("resetCreditsSummary").GetString(), StringComparison.Ordinal);
        Assert.Equal(color, data.RootElement.GetProperty("resetCreditsColor").GetString());
    }
}
