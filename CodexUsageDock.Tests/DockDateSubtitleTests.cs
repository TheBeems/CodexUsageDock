using System.Globalization;
using Xunit;

namespace CodexUsageDock.Tests;

public sealed class DockDateSubtitleTests
{
    [Theory]
    [InlineData(9, 15, 12, 56, "15 sept 12:56")]
    [InlineData(10, 4, 4, 0, "4 okt 4:00")]
    [InlineData(1, 1, 0, 5, "1 jan 0:05")]
    public void DutchDatesUseAbbreviatedMonthsAndUnpaddedHours(int month, int day, int hour, int minute, string expected)
    {
        var local = new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(2));
        Assert.Equal(expected, UsageDockItem.FormatLocalDateTime(local, CultureInfo.GetCultureInfo("nl-NL")));
    }

    [Theory]
    [InlineData("Reset - 15 sept 12:56")]
    [InlineData("Expires - 4 okt 4:00")]
    public void FreshLiveSubtitleShowsOnlyTheDateDetail(string detail)
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var snapshot = CodexUsageSnapshot.Loading with { Source = UsageDataSource.AppServer, UpdatedAt = now.AddMinutes(-10) };
        Assert.Equal(detail, UsageDockItem.FormatLiveDetailOrStatus(snapshot, now, TimeSpan.FromMinutes(15), detail));
    }

    [Fact]
    public void StaleAndFallbackSubtitlesRetainTheirWarning()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var stale = CodexUsageSnapshot.Loading with { Source = UsageDataSource.AppServer, UpdatedAt = now.AddHours(-1) };
        Assert.StartsWith("Stale", UsageDockItem.FormatLiveDetailOrStatus(stale, now, TimeSpan.FromMinutes(1), "Reset - 2 jan 4:00"));
        var fallback = stale with { Source = UsageDataSource.LastConfirmed };
        Assert.StartsWith("Last confirmed", UsageDockItem.FormatLiveDetailOrStatus(fallback, now, TimeSpan.FromMinutes(1), "Expires - 2 jan 4:00"));
        var local = stale with { Source = UsageDataSource.LocalSession, UpdatedAt = now };
        Assert.StartsWith("Fallback", UsageDockItem.FormatLiveDetailOrStatus(local, now, TimeSpan.FromMinutes(1), "Reset - 2 jan 4:00"));
    }

    [Fact]
    public void HiddenDateRetainsExistingStatusText()
    {
        var now = DateTimeOffset.UnixEpoch;
        var snapshot = CodexUsageSnapshot.Loading with { Source = UsageDataSource.AppServer, UpdatedAt = now };
        Assert.Equal(UsageDockItem.FormatSourceFreshness(snapshot, now),
            UsageDockItem.FormatLiveDetailOrStatus(snapshot, now, TimeSpan.FromMinutes(1), string.Empty));
    }
}
