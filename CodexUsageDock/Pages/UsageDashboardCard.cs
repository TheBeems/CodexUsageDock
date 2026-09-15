using System.Globalization;
using System.Xml.Linq;

namespace CodexUsageDock;

internal enum UsageBarPalette
{
    Used,
    Time,
}

internal static class UsageDashboardCard
{
    internal const int BarHeight = 12;
    internal const int BarWidth = 536;


    internal const string TemplateJson = """
        {
          "type": "AdaptiveCard",
          "body": [
            {
              "type": "ColumnSet",
              "columns": [
                {
                  "type": "Column", "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock", "text": "${notice}",
                      "wrap": true, "color": "Warning", "$when": "${hasNotice}"
                    },
                    {
                      "type": "TextBlock", "text": "${resetCreditsSummary}",
                      "wrap": true, "color": "${resetCreditsColor}",
                      "$when": "${hasResetCredits}"
                    },
                    {
                      "type": "TextBlock", "text": "Refreshing…",
                      "isSubtle": true, "$when": "${isLoading}"
                    }
                  ]
                },
                {
                  "type": "Column", "width": "auto",
                  "items": [
                    {
                      "type": "ActionSet",
                      "actions": [
                        {
                          "type": "Action.Submit", "title": "${detailsButtonTitle}",
                          "data": { "action": "details" }
                        }
                      ]
                    }
                  ]
                }
              ]
            },
            {
              "type": "Container",
              "$data": "${quotaGroups}",
              "spacing": "medium", "separator": true,
              "items": [
                { "type": "TextBlock", "text": "${heading}", "weight": "bolder", "wrap": true },
                {
                  "type": "TextBlock", "text": "${inactiveWindows}",
                  "isSubtle": true, "spacing": "small", "wrap": true,
                  "$when": "${hasInactiveWindows}"
                },
                {
                  "type": "ColumnSet", "spacing": "small",
                  "columns": [
                    {
                      "type": "Column", "width": "stretch", "$data": "${windows}",
                      "items": [
                        {
                          "type": "TextBlock", "text": "${title}", "weight": "bolder", "wrap": true,
                          "$when": "${showTitle}"
                        },
                        {
                          "type": "ColumnSet", "spacing": "small",
                          "columns": [
                            {
                              "type": "Column", "width": "stretch",
                              "items": [ { "type": "TextBlock", "text": "Used", "isSubtle": true } ]
                            },
                            {
                              "type": "Column", "width": "auto",
                              "items": [
                                {
                                  "type": "TextBlock", "text": "${usedPercent}",
                                  "size": "large", "weight": "bolder", "color": "${usedColor}",
                                  "horizontalAlignment": "right"
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "Image", "url": "${usedBarUrl}", "altText": "${usedBarAlt}",
                          "size": "stretch", "height": "12px", "spacing": "none"
                        },
                        {
                          "type": "ColumnSet", "spacing": "small",
                          "columns": [
                            {
                              "type": "Column", "width": "stretch",
                              "items": [ { "type": "TextBlock", "text": "Time elapsed", "isSubtle": true } ]
                            },
                            {
                              "type": "Column", "width": "auto",
                              "items": [
                                { "type": "TextBlock", "text": "${elapsedPercent}", "horizontalAlignment": "right" }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "Image", "url": "${elapsedBarUrl}", "altText": "${elapsedBarAlt}",
                          "size": "stretch", "height": "12px", "spacing": "none", "$when": "${elapsedAvailable}"
                        },
                        {
                          "type": "TextBlock", "text": "${reset}",
                          "isSubtle": true, "spacing": "small", "wrap": true
                        }
                      ]
                    },
                    {
                      "type": "Column", "width": "stretch", "items": [],
                      "$when": "${singleWindow}"
                    }
                  ]
                }
              ]
            },
            {
              "type": "Container", "separator": true, "spacing": "medium",
              "$when": "${weeklyAvailable}",
              "items": [
                { "type": "TextBlock", "text": "Codex · weekly usage", "weight": "bolder" },
                {
                  "type": "Image", "url": "${weeklyTrendChartUrl}", "altText": "${weeklyTrendChartAlt}",
                  "size": "stretch", "spacing": "small", "$when": "${weeklyTrendAvailable}"
                },
                {
                  "type": "TextBlock", "text": "${weeklyForecastSummary}",
                  "isSubtle": true, "wrap": true, "spacing": "small"
                }
              ]
            }
          ],
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5"
        }
        """;

    internal static string CreateProgressBarImageUrl(double percent, UsageBarPalette palette)
    {
        var normalized = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
        var progressWidth = (BarWidth - 2d) * normalized / 100;
        var fillColor = palette switch
        {
            UsageBarPalette.Time => "#8A8A8A",
            _ => "#5C9EFA",
        };

        XNamespace svg = "http://www.w3.org/2000/svg";
        var document = new XElement(
            svg + "svg",
            new XAttribute("width", BarWidth),
            new XAttribute("height", BarHeight),
            new XAttribute("viewBox", $"0 0 {BarWidth} {BarHeight}"),
            new XElement(
                svg + "rect",
                new XAttribute("x", "0.5"),
                new XAttribute("y", "0.5"),
                new XAttribute("width", BarWidth - 1),
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
