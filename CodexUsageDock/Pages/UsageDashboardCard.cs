using System.Globalization;
using System.Xml.Linq;

namespace CodexUsageDock;

internal enum UsageBarPalette
{
    Remaining,
    Time,
}

internal static class UsageDashboardCard
{
    internal const int BarHeight = 12;
    internal const int BarWidth = 536;


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
                      "type": "ColumnSet", "spacing": "none",
                      "columns": [
                        {
                          "type": "Column", "width": "stretch",
                          "items": [ { "type": "TextBlock", "text": "Remaining", "isSubtle": true } ]
                        },
                        {
                          "type": "Column", "width": "auto",
                          "items": [
                            {
                              "type": "TextBlock", "text": "${remainingPercent}",
                              "size": "${remainingSize}", "weight": "bolder", "color": "${remainingColor}",
                              "horizontalAlignment": "right"
                            }
                          ]
                        }
                      ]
                    },
                    {
                      "type": "Image", "url": "${remainingBarUrl}", "altText": "${remainingBarAlt}",
                      "size": "stretch", "spacing": "none"
                    },
                    {
                      "type": "ColumnSet", "spacing": "none",
                      "columns": [
                        {
                          "type": "Column", "width": "stretch",
                          "items": [ { "type": "TextBlock", "text": "Time elapsed", "isSubtle": true } ]
                        },
                        {
                          "type": "Column", "width": "auto",
                          "items": [ { "type": "TextBlock", "text": "${elapsedPercent}", "horizontalAlignment": "right" } ]
                        }
                      ]
                    },
                    {
                      "type": "Image", "url": "${elapsedBarUrl}", "altText": "${elapsedBarAlt}",
                      "size": "stretch", "spacing": "none", "$when": "${elapsedAvailable}"
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
            {
              "type": "TextBlock", "text": "${weeklyForecastSummary}", "wrap": true,
              "weight": "bolder", "color": "${forecastColor}", "spacing": "small", "$when": "${hasWeeklyForecast}"
            },
            { "type": "TextBlock", "text": "Refreshing…", "isSubtle": true, "$when": "${isLoading}" },
            {{QuotaGroupTemplate}},
            {
              "type": "Container", "spacing": "small", "$when": "${weeklyTrendAvailable}",
              "items": [
                { "type": "TextBlock", "text": "Codex · weekly remaining", "weight": "bolder" },
                {
                  "type": "Image", "url": "${weeklyTrendChartUrl}", "altText": "${weeklyTrendChartAlt}",
                  "size": "stretch", "spacing": "small"
                },
                {
                  "type": "TextBlock", "text": "${chartLegend} · dashed: estimate",
                  "isSubtle": true, "size": "small", "wrap": true, "spacing": "small"
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

    internal static string CreateProgressBarImageUrl(double percent, UsageBarPalette palette, int columns = 2)
    {
        // The host preserves SVG aspect ratio. Match intrinsic width to each column's share
        // so a full-width window does not render a bar twice as thick as paired windows.
        var width = BarWidth * 2d / Math.Max(1, columns);
        var normalized = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
        var progressWidth = (width - 2d) * normalized / 100;
        var fillColor = palette switch
        {
            UsageBarPalette.Time => "#8A8A8A",
            _ => "#5C9EFA",
        };

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
                    new XAttribute("fill", fillColor)));
        }

        return CreateSvgImageUrl(document);
    }

    internal static string CreateSvgImageUrl(XElement document)
    {
        var encodedSvg = Uri.EscapeDataString(document.ToString(SaveOptions.DisableFormatting));
        return $"data:image/svg+xml;utf8,{encodedSvg}";
    }
}
