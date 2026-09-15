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

    private static JsonDocument Render(CodexUsageSnapshot snapshot, bool details = false, UsageHistoryEntry[]? history = null) =>
        JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(snapshot, Now, false, [],
            history ?? [], TimeSpan.FromMinutes(1),
            tokenUsage: new LocalTokenUsageSnapshot([new DailyTokenUsage(new DateOnly(2026, 9, 15), 1000)],
                Now, LocalTokenUsageStatus.Complete),
            showDetails: details));

    private static XDocument Chart(JsonElement data) => XDocument.Parse(
        Uri.UnescapeDataString(data.GetProperty("weeklyTrendChartUrl").GetString()!["data:image/svg+xml;utf8,".Length..]));

    [Fact]
    public void CodexAndSparkShareUsedAndTimeBarsWithoutCombiningCategories()
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
        Assert.Equal("25%", codex[0].GetProperty("usedPercent").GetString());
        Assert.Equal("20%", spark[0].GetProperty("elapsedPercent").GetString());
        Assert.Equal(codex[0].GetProperty("usedBarUrl").GetString(), spark[0].GetProperty("usedBarUrl").GetString());
        Assert.Equal(codex[0].GetProperty("elapsedBarUrl").GetString(), spark[0].GetProperty("elapsedBarUrl").GetString());
        Assert.Equal("33%", codex[1].GetProperty("usedPercent").GetString());
        Assert.Equal("80%", spark[1].GetProperty("usedPercent").GetString());
        Assert.DoesNotContain("available", codex[0].GetProperty("usedBarAlt").GetString()!, StringComparison.Ordinal);
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
        Assert.Equal("0%", zero.GetProperty("usedPercent").GetString());
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
            Assert.Contains("last observation", window.GetProperty("usedBarAlt").GetString(), StringComparison.Ordinal);
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
    public void DefaultChartRisesWithUsageAndDetailsOnlyAddsTheTokenOverlay()
    {
        UsageHistoryEntry[] history = [new(Now.AddMinutes(-5), 80), new(Now, 67)];
        using var compact = Render(Snapshot(), history: history);
        using var expanded = Render(Snapshot(), details: true, history: history);
        var chart = Chart(compact.RootElement);
        var detailChart = Chart(expanded.RootElement);
        Assert.DoesNotContain(chart.Descendants(Svg + "rect"), node => node.Attribute("data-series")?.Value == "daily-tokens");
        Assert.Single(detailChart.Descendants(Svg + "rect"), node => node.Attribute("data-series")?.Value == "daily-tokens");
        var line = Assert.Single(chart.Descendants(Svg + "polyline"));
        var points = line.Attribute("points")!.Value.Split(' ');
        var firstY = double.Parse(points[0].Split(',')[1], CultureInfo.InvariantCulture);
        var lastY = double.Parse(points[^1].Split(',')[1], CultureInfo.InvariantCulture);
        Assert.True(lastY < firstY, "Increasing usage must move up the used-quota axis.");
        Assert.Equal(line.Attribute("points")!.Value, Assert.Single(detailChart.Descendants(Svg + "polyline")).Attribute("points")!.Value);
        Assert.Contains("20% to 33%", compact.RootElement.GetProperty("weeklyTrendChartAlt").GetString(), StringComparison.Ordinal);
        Assert.NotEmpty(chart.Descendants(Svg + "path"));
    }

    [Fact]
    public void DetailsToggleSurvivesRefreshAndClosesAgain()
    {
        using var service = _environment.CreateService();
        using var page = new CodexUsageDockPage(service, _environment.CreateSettings());
        var form = Assert.IsAssignableFrom<FormContent>(Assert.Single(page.GetContent()));
        Assert.Null(page.Details);
        form.SubmitForm("""{"action":"details"}""");
        var details = Assert.IsType<Details>(page.Details);
        Assert.Contains("Forecast and budget", details.Body, StringComparison.Ordinal);
        page.Refresh();
        Assert.Same(details, page.Details);
        using (var data = JsonDocument.Parse(form.DataJson))
            Assert.Equal("Hide details", data.RootElement.GetProperty("detailsButtonTitle").GetString());
        form.SubmitForm("""{"action":"details"}""");
        Assert.Null(page.Details);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"action":true}""")]
    [InlineData("""{"action":"reset"}""")]
    public void UnrecognizedFormInputCannotToggleDetails(string payload)
    {
        using var service = _environment.CreateService();
        using var page = new CodexUsageDockPage(service, _environment.CreateSettings());
        Assert.IsAssignableFrom<FormContent>(Assert.Single(page.GetContent())).SubmitForm(payload);
        Assert.Null(page.Details);
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
