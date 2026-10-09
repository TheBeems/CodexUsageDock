using System.Globalization;
using System.Xml.Linq;

namespace CodexUsageDock;

internal static class UsageDashboardCard
{
    internal const int BarHeight = 12;
    internal const int BarWidth = 536;
    internal const string TimeBarFill = "#73879B";


    // Reuse the same windows before and after the primary chart.
    private const string QuotaGroupTemplate = """
        {
          "type": "Container", "$data": "${quotaGroups}", "$when": "${isPrimary}",
          "spacing": "small",
          "items": [
            { "type": "TextBlock", "text": "${heading}", "weight": "bolder", "wrap": true },
            {
              "type": "ColumnSet", "spacing": "none",
              "columns": [
                {
                  "type": "Column", "width": "stretch", "$data": "${windows}",
                  "spacing": "extraLarge", "separator": "${separate}",
                  "items": [
                    { "type": "TextBlock", "text": "${title}", "weight": "bolder", "wrap": true, "$when": "${showTitle}" },
                    {
                      "type": "TextBlock", "text": "${remainingHeadline}",
                      "size": "large", "weight": "bolder", "color": "${remainingColor}", "wrap": true, "spacing": "none"
                    },
                    {
                      "type": "Image", "url": "${remainingBarUrl}", "altText": "${remainingBarAlt}",
                      "size": "stretch", "spacing": "none"
                    },
                    { "type": "TextBlock", "text": "${timeRemainingHeadline}", "isSubtle": true, "size": "small", "spacing": "small", "wrap": true, "$when": "${elapsedAvailable}" },
                    {
                      "type": "Image", "url": "${timeRemainingBarUrl}", "altText": "${timeRemainingBarAlt}",
                      "size": "stretch", "spacing": "none", "$when": "${elapsedAvailable}"
                    },
                    { "type": "TextBlock", "text": "${usageComparison}", "isSubtle": true, "size": "small", "spacing": "small", "wrap": true, "$when": "${!elapsedAvailable}" },
                    {
                      "type": "TextBlock", "text": "${weeklyForecastSummary}", "wrap": true,
                      "color": "${forecastColor}", "weight": "bolder", "spacing": "small", "$when": "${hasWeeklyForecast}"
                    },
                    { "type": "TextBlock", "text": "${reset}", "isSubtle": true, "size": "small", "spacing": "none", "wrap": true }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock", "text": "${inactiveWindows}", "isSubtle": true,
              "size": "small", "spacing": "small", "wrap": true, "$when": "${hasInactiveWindows}"
            }
          ]
        }
        """;

    internal static readonly string TemplateJson = $$"""
        {
          "type": "AdaptiveCard",
          "body": [
            { "type": "TextBlock", "text": "${notice}", "wrap": true, "color": "Warning", "$when": "${hasNotice}" },
            { "type": "TextBlock", "text": "Refreshing…", "isSubtle": true, "$when": "${isLoading}" },
            {{QuotaGroupTemplate}},
            {
              "type": "Container", "spacing": "small", "$when": "${weeklyTrendAvailable}",
              "items": [
                {
                  "type": "Image", "url": "${weeklyTrendChartUrl}", "altText": "${weeklyTrendChartAlt}",
                  "size": "stretch", "spacing": "none"
                },
                {
                  "type": "TextBlock", "text": "${weeklyForecastStatus}", "wrap": true,
                  "isSubtle": true, "size": "small", "spacing": "small", "$when": "${hasForecastBasis}"
                }
              ]
            },
            {{QuotaGroupTemplate.Replace("${isPrimary}", "${!isPrimary}", StringComparison.Ordinal)}},
            {
              "type": "TextBlock", "text": "${resetCreditsSummary}", "wrap": true,
              "size": "small", "color": "${resetCreditsColor}", "$when": "${hasResetCredits}", "spacing": "medium"
            }
          ],
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5"
        }
        """;

    internal static string CreateProgressBarImageUrl(double percent, int columns = 2, string fill = "#5C9EFA")
    {
        // The host preserves SVG aspect ratio. Match intrinsic width to each column's share
        // so a full-width window does not render a bar twice as thick as paired windows.
        var width = BarWidth * 2d / Math.Max(1, columns);
        var normalized = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
        var progressWidth = (width - 2d) * normalized / 100;

        XNamespace svg = "http://www.w3.org/2000/svg";
        var document = new XElement(
            svg + "svg",
            new XAttribute("width", width),
            new XAttribute("height", BarHeight),
            new XAttribute("viewBox", FormattableString.Invariant($"0 0 {width} {BarHeight}")),
            new XElement(
                svg + "rect",
                new XAttribute("x", "0.5"),
                new XAttribute("y", "0.5"),
                new XAttribute("width", width - 1),
                new XAttribute("height", BarHeight - 1),
                new XAttribute("rx", "4.5"),
                new XAttribute("fill", "#7A7A7A"),
                new XAttribute("fill-opacity", "0.2"),
                new XAttribute("stroke", "#7A7A7A"),
                new XAttribute("stroke-opacity", "0.65")));
        if (progressWidth > 0)
        {
            document.Add(
                new XElement(
                    svg + "rect",
                    new XAttribute("x", "1"),
                    new XAttribute("y", "1"),
                    new XAttribute("width", progressWidth.ToString("0.##", CultureInfo.InvariantCulture)),
                    new XAttribute("height", BarHeight - 2),
                    new XAttribute("rx", "4"),
                    new XAttribute("fill", fill)));
        }

        return CreateSvgImageUrl(document);
    }

    internal static string CreateSvgImageUrl(XElement document)
    {
        var encodedSvg = Uri.EscapeDataString(document.ToString(SaveOptions.DisableFormatting));
        return $"data:image/svg+xml;utf8,{encodedSvg}";
    }
}
