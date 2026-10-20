using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class AccountTokenChartTests : IDisposable
{
    private const string Account = "synthetic-chart-account";
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly TokenDate = new(2026, 9, 8);
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    private static CodexUsageSnapshot Quota() => CodexUsageSnapshot.Loading with
    {
        Secondary = new(20, 10080, Now.AddDays(6)),
        UpdatedAt = Now,
        Source = UsageDataSource.AppServer,
        AccountKey = Account,
        DefaultBucketId = "codex",
    };

    private static AccountUsageSnapshot Tokens(AccountUsageStatus status = AccountUsageStatus.Available) =>
        new(Account, Now, [new(TokenDate, 890_123_456)], status);

    private static JsonDocument Dashboard(AccountUsageSnapshot accountUsage, CodexUsageSnapshot? quota = null,
        bool enabled = true) => JsonDocument.Parse(CodexUsageDockPage.FormatMainDataJson(
            quota ?? Quota(), Now, false, [], [new(Now.AddMinutes(-1), 81), new(Now, 80)], TimeSpan.FromMinutes(1),
            accountUsage: accountUsage, showAccountActivity: enabled));

    private static XDocument ParseChart(string imageUrl) =>
        XDocument.Parse(Uri.UnescapeDataString(imageUrl["data:image/svg+xml;utf8,".Length..]));

    private static IEnumerable<XElement> TokenBars(XDocument chart) =>
        chart.Descendants(Svg + "rect").Where(rect => rect.Attribute("data-series")?.Value == "daily-tokens");

    [Fact]
    public async Task AccountTotalsReachTheMainChartIndependentlyOfLocalTokensAndQuotaRefresh()
    {
        var pending = new TaskCompletionSource<AccountUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quota = Quota();
        using var service = _environment.CreateService(_ => Task.FromResult(quota), () => quota,
            localTokenUsageReader: (_, _, _, _) => Task.FromResult(new LocalTokenUsageSnapshot(
                [new(TokenDate, 12_345)], Now, LocalTokenUsageStatus.Complete)),
            clock: () => Now, accountUsageReader: _ => pending.Task);

        await service.RefreshAsync();
        await service.TokenRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(service.AccountUsageRefreshTask.IsCompleted);
        var before = service.GetPresentation();
        using (var loading = Dashboard(before.AccountUsage, before.Usage))
        {
            Assert.Empty(TokenBars(ParseChart(loading.RootElement.GetProperty("weeklyTrendChartUrl").GetString()!)));
            Assert.Contains("unavailable", loading.RootElement.GetProperty("weeklyTrendLegend").GetString(), StringComparison.Ordinal);
        }

        var updates = 0;
        service.Updated += (_, _) => Interlocked.Increment(ref updates);
        var accountUsage = Tokens();
        pending.SetResult(accountUsage);
        await service.AccountUsageRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));

        var view = service.GetPresentation();
        Assert.Same(accountUsage, view.AccountUsage);
        Assert.Equal(12_345, Assert.Single(view.TokenUsage.Days).TotalTokens);
        Assert.True(updates > 0);
        using var dashboard = Dashboard(view.AccountUsage, view.Usage);
        var data = dashboard.RootElement;
        var bar = Assert.Single(TokenBars(ParseChart(data.GetProperty("weeklyTrendChartUrl").GetString()!)));
        Assert.Equal("890123456", bar.Attribute("data-tokens")?.Value);
        Assert.Equal("2026-09-08", bar.Attribute("data-date")?.Value);
        Assert.Contains("Codex account service", data.GetProperty("weeklyTrendLegend").GetString(), StringComparison.Ordinal);
        Assert.Contains("Last read:", data.GetProperty("weeklyTrendLegend").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("local tokens", data.GetProperty("weeklyTrendChartAlt").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerDatesAndWholeDayTotalsSurviveTimeZoneAndQuotaBoundaryChanges()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("UTC+14 test", TimeSpan.FromHours(14), "UTC+14 test", "UTC+14 test");
        var tokens = new AccountUsageSnapshot(Account, Now,
            [new(new(2026, 9, 8), 0), new(new(2026, 9, 9), 765_432)], AccountUsageStatus.Partial);
        var chart = Assert.IsType<WeeklyUsageTrendChart>(WeeklyUsageTrendChartRenderer.Create(
            [new(Now.AddDays(-3), 95), new(Now, 80)], Quota().Secondary!, Now, TimeSpan.FromDays(4),
            null, tokens, CultureInfo.InvariantCulture, timeZone));

        var svg = ParseChart(chart.ImageUrl);
        var bar = Assert.Single(TokenBars(svg));
        Assert.Equal("2026-09-09", bar.Attribute("data-date")?.Value);
        Assert.Equal("765432", bar.Attribute("data-tokens")?.Value);
        Assert.Contains("Mon 7 Sep: not reported", chart.AltText, StringComparison.Ordinal);
        Assert.Contains("Tue 8 Sep: 0", chart.AltText, StringComparison.Ordinal);
        Assert.Contains("Account token data is partial", chart.AltText, StringComparison.Ordinal);
        Assert.Contains("time zone is unspecified", chart.AltText, StringComparison.Ordinal);
        Assert.Contains("not clipped to the quota window", chart.AltText, StringComparison.Ordinal);
        Assert.Contains(svg.Descendants(Svg + "g"), group =>
            group.Attribute("data-axis-label")?.Value == "Account tokens/day (right axis)");
    }

    [Theory]
    [InlineData(false, "unavailable")]
    [InlineData(true, "unsupported")]
    public void UnavailableAccountDataOmitsTokenBarsWithoutRemovingTheQuotaTrend(bool unsupported, string label)
    {
        using var dashboard = Dashboard(Tokens(unsupported ? AccountUsageStatus.Unsupported : AccountUsageStatus.Unavailable));
        var data = dashboard.RootElement;
        var chart = ParseChart(data.GetProperty("weeklyTrendChartUrl").GetString()!);

        Assert.Empty(TokenBars(chart));
        Assert.NotEmpty(chart.Descendants(Svg + "polyline"));
        Assert.Contains(label, data.GetProperty("weeklyTrendLegend").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledAccountActivityDoesNotShowCachedAccountTotals()
    {
        using var dashboard = Dashboard(Tokens(), enabled: false);
        var data = dashboard.RootElement;

        Assert.Empty(TokenBars(ParseChart(data.GetProperty("weeklyTrendChartUrl").GetString()!)));
        Assert.Contains("disabled in settings", data.GetProperty("weeklyTrendLegend").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("another-account")]
    public void UnknownOrDifferentQuotaAccountCannotDisplayAccountTotals(string? quotaAccount)
    {
        using var dashboard = Dashboard(Tokens(), Quota() with { AccountKey = quotaAccount });
        var data = dashboard.RootElement;

        Assert.Empty(TokenBars(ParseChart(data.GetProperty("weeklyTrendChartUrl").GetString()!)));
        Assert.Contains("unavailable", data.GetProperty("weeklyTrendLegend").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryWithoutDailyBucketsDoesNotInventZeroDays()
    {
        using var dashboard = Dashboard(new(Account, Now, [], AccountUsageStatus.Partial, LifetimeTokens: 123_456));
        var data = dashboard.RootElement;

        Assert.Empty(TokenBars(ParseChart(data.GetProperty("weeklyTrendChartUrl").GetString()!)));
        Assert.Contains("not reported", data.GetProperty("weeklyTrendChartAlt").GetString(), StringComparison.Ordinal);
        Assert.Contains("partial account data", data.GetProperty("weeklyTrendLegend").GetString(), StringComparison.Ordinal);
    }
}
