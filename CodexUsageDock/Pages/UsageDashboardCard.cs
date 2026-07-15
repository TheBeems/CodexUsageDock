using System.Globalization;
using System.Xml.Linq;

namespace CodexUsageDock;

internal enum UsageBarPalette
{
    FiveHour,
    Weekly,
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
              "type": "TextBlock",
              "text": "Refreshing Codex usage…",
              "weight": "bolder",
              "wrap": true,
              "$when": "${isLoading}"
            },
            {
              "type": "Container",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${statusTitle}",
                  "weight": "bolder",
                  "wrap": true
                },
                {
                  "type": "TextBlock",
                  "text": "${statusDescription}",
                  "spacing": "none",
                  "wrap": true
                }
              ]
            },
            {
              "type": "Container",
              "separator": true,
              "spacing": "medium",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "5-hour window",
                  "size": "large",
                  "weight": "bolder"
                },
                {
                  "type": "TextBlock",
                  "text": "${fiveHourState}",
                  "weight": "bolder",
                  "wrap": true,
                  "$when": "${fiveHourAvailable == false}"
                },
                {
                  "type": "ColumnSet",
                  "$when": "${fiveHourAvailable}",
                  "columns": [
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "Available",
                          "isSubtle": true
                        },
                        {
                          "type": "TextBlock",
                          "text": "${fiveHourRemaining}",
                          "size": "large",
                          "weight": "bolder",
                          "spacing": "none"
                        }
                      ]
                    },
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "Reset",
                          "isSubtle": true,
                          "horizontalAlignment": "right"
                        },
                        {
                          "type": "TextBlock",
                          "text": "${fiveHourReset}",
                          "horizontalAlignment": "right",
                          "spacing": "none",
                          "wrap": true
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "ColumnSet",
                  "spacing": "medium",
                  "$when": "${fiveHourAvailable}",
                  "columns": [
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "Allowance used",
                          "isSubtle": true
                        }
                      ]
                    },
                    {
                      "type": "Column",
                      "width": "auto",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "${fiveHourUsedPercent}",
                          "weight": "bolder",
                          "horizontalAlignment": "right"
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "Image",
                  "url": "${fiveHourUsedBarUrl}",
                  "altText": "${fiveHourUsedBarAlt}",
                  "size": "stretch",
                  "spacing": "none",
                  "$when": "${fiveHourAvailable}"
                },
                {
                  "type": "ColumnSet",
                  "spacing": "small",
                  "$when": "${fiveHourAvailable}",
                  "columns": [
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "Window elapsed",
                          "isSubtle": true
                        }
                      ]
                    },
                    {
                      "type": "Column",
                      "width": "auto",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "${fiveHourElapsedPercent}",
                          "weight": "bolder",
                          "horizontalAlignment": "right"
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "Image",
                  "url": "${fiveHourElapsedBarUrl}",
                  "altText": "${fiveHourElapsedBarAlt}",
                  "size": "stretch",
                  "spacing": "none",
                  "$when": "${fiveHourAvailable}"
                },
                {
                  "type": "TextBlock",
                  "text": "${fiveHourPaceStatus}",
                  "color": "${fiveHourPaceColor}",
                  "weight": "bolder",
                  "wrap": true,
                  "$when": "${fiveHourAvailable}"
                },
                {
                  "type": "TextBlock",
                  "text": "${fiveHourProjection}",
                  "isSubtle": true,
                  "spacing": "small",
                  "wrap": true,
                  "$when": "${fiveHourAvailable}"
                }
              ]
            },
            {
              "type": "Container",
              "separator": true,
              "spacing": "medium",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "Weekly window",
                  "size": "large",
                  "weight": "bolder"
                },
                {
                  "type": "TextBlock",
                  "text": "${weeklyState}",
                  "weight": "bolder",
                  "wrap": true,
                  "$when": "${weeklyAvailable == false}"
                },
                {
                  "type": "ColumnSet",
                  "$when": "${weeklyAvailable}",
                  "columns": [
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "Available",
                          "isSubtle": true
                        },
                        {
                          "type": "TextBlock",
                          "text": "${weeklyRemaining}",
                          "size": "large",
                          "weight": "bolder",
                          "spacing": "none"
                        }
                      ]
                    },
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "Reset",
                          "isSubtle": true,
                          "horizontalAlignment": "right"
                        },
                        {
                          "type": "TextBlock",
                          "text": "${weeklyReset}",
                          "horizontalAlignment": "right",
                          "spacing": "none",
                          "wrap": true
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "ColumnSet",
                  "spacing": "medium",
                  "$when": "${weeklyAvailable}",
                  "columns": [
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "Allowance used",
                          "isSubtle": true
                        }
                      ]
                    },
                    {
                      "type": "Column",
                      "width": "auto",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "${weeklyUsedPercent}",
                          "weight": "bolder",
                          "horizontalAlignment": "right"
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "Image",
                  "url": "${weeklyUsedBarUrl}",
                  "altText": "${weeklyUsedBarAlt}",
                  "size": "stretch",
                  "spacing": "none",
                  "$when": "${weeklyAvailable}"
                },
                {
                  "type": "ColumnSet",
                  "spacing": "small",
                  "$when": "${weeklyAvailable}",
                  "columns": [
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "Window elapsed",
                          "isSubtle": true
                        }
                      ]
                    },
                    {
                      "type": "Column",
                      "width": "auto",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "${weeklyElapsedPercent}",
                          "weight": "bolder",
                          "horizontalAlignment": "right"
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "Image",
                  "url": "${weeklyElapsedBarUrl}",
                  "altText": "${weeklyElapsedBarAlt}",
                  "size": "stretch",
                  "spacing": "none",
                  "$when": "${weeklyAvailable}"
                },
                {
                  "type": "TextBlock",
                  "text": "${weeklyPaceStatus}",
                  "color": "${weeklyPaceColor}",
                  "weight": "bolder",
                  "wrap": true,
                  "$when": "${weeklyAvailable}"
                },
                {
                  "type": "TextBlock",
                  "text": "${weeklyProjection}",
                  "isSubtle": true,
                  "spacing": "small",
                  "wrap": true,
                  "$when": "${weeklyAvailable}"
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
            UsageBarPalette.FiveHour => "#39B8E3",
            UsageBarPalette.Weekly => "#5C9EFA",
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

        var encodedSvg = Uri.EscapeDataString(document.ToString(SaveOptions.DisableFormatting));
        return $"data:image/svg+xml;utf8,{encodedSvg}";
    }
}
