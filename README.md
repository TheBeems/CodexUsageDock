# Codex Usage Dock

See your remaining Codex usage at a glance in the PowerToys Dock. Codex Usage Dock is a Windows Command Palette extension for tracking limits, reset times, and usage trends.

- Check your remaining five-hour and weekly allowance.
- Explore usage trends, pace indicators, and forecasts.
- See available earned resets, their expiry times, and credits when Codex provides them.
- Customize the Dock and enable optional usage alerts.

**[Install from Microsoft Store](https://apps.microsoft.com/detail/9NFCPJXQG9FG)**

[Quick start](#quick-start) · [Understand your usage](#understand-your-usage) · [Settings](#settings) · [Advanced features](#advanced-features) · [Troubleshooting](#troubleshooting)

## Quick start

### Requirements

- Windows 10 build 19041 or newer, or Windows 11.
- [PowerToys 0.100.0 or newer](https://github.com/microsoft/PowerToys), with Command Palette enabled.
- For live data, a standalone Codex CLI installed and signed in, with `codex.exe` or `codex.cmd` on `PATH`. Alternatively, set `CODEX_USAGE_DOCK_CODEX_PATH` to its full path and restart PowerToys.

**The Microsoft Store Codex Desktop app alone is not sufficient for live data.** Its protected bundled CLI cannot be launched by this extension. Local session metadata can provide fallback data when available.

To check what is available on `PATH`, run `where.exe codex` in a terminal. A result must point to a standalone CLI; finding the protected Desktop copy does not satisfy this requirement.

The package includes the required .NET runtime. You do not need the .NET SDK to use it.

### Install and add to the Dock

1. Install **Codex Usage Dock** using the Microsoft Store link above.
2. Open Command Palette settings, select **Extensions**, and enable **Codex Usage**.
3. Select **Dock (Preview)** and enable the Dock.
4. Open Dock customization and choose **Add command** (`+`) in the section where you want the widget.
5. Search for **Codex Usage** and select its Dock band.

The extension runs inside Command Palette and has no standalone Start-menu entry. Select a Dock entry to open usage details or refresh manually.

### Updates and removal

Microsoft Store is the only production installation and update channel. Check **Microsoft Store > Library** for updates and **Windows Settings > Apps > Installed apps** for the installed version or to uninstall.

GitHub releases identify source versions and may differ from the version available in the Store. GitHub Actions artifacts are certification inputs, not public installers; do not install or distribute them directly.

## Understand your usage

| Display | Meaning |
| --- | --- |
| `5h 47%` | 47% of the five-hour allowance remains. |
| `Week 86%` | 86% of the weekly allowance remains. |
| `2 resets · 10.00` | Two earned resets are available; `10.00` is the reported credits balance. |
| `Reset - …` / `Expires - …` | The next quota reset or earned-reset expiry, using your local time zone and regional date format. |

Percentages show **remaining allowance**, not usage already consumed. Earned resets and the credits balance are separate values; missing information is not treated as zero. The overall status reflects the most restrictive active quota window.

Values refresh every minute by default. Details shows the data source and measurement time. During an outage, **Last confirmed** can retain the previous live measurement with its original timestamp. Local fallback data is labeled with its source and age.

Forecasts estimate future allowance from observed usage. They pause when data is stale or there are too few fresh, continuous measurements. Daily token bars reflect local session activity and are **not an exact measure of quota consumption**.

See the [user guide](USER_GUIDE.md#charts-and-forecasts) for chart legends, adaptive forecasts, and data handling.

## Settings

Open Command Palette and select **Codex Usage settings**.

| Option | What it changes |
| --- | --- |
| Visible metrics and reset times | Show or hide the five-hour limit, weekly limit, resets and credits, and quota reset times. |
| **Compact Dock** | Use shorter labels such as `5h47%` and `W86%`, hiding reset times while keeping source warnings. |
| **Separate Dock items** | Pin metrics individually instead of using one combined band. |
| Refresh interval | Choose 1, 5, or 15 minutes. |
| Adaptive weekly forecast | Enable or pause learning from local usage history. Pausing keeps learned history; deletion requires confirmation. |
| **Enable usage alerts** | Receive optional low-allowance, projected-limit, and expiring-reset notifications. Off by default. |
| **Show account activity** | Enable optional account-wide usage reads on compatible CLI versions. |
| **Retain usage observations** | Enable optional history retention for 7, 30, or 90 days. |

Switching between combined and separate items does not convert existing pins. Add the desired bands through Dock customization; inactive bands stay hidden. Settings persist after restart, and saving failures are shown on the settings page.

See [Dock settings](USER_GUIDE.md#dock-settings) for pin behavior, alert conditions, and saved preferences.

## Advanced features

These pages are available in Command Palette:

| Page | Purpose |
| --- | --- |
| **Codex usage in text** | Read quotas, reset times, trends, and token totals without relying on charts or color. Also available from Details. |
| **Codex account activity** | View account-wide token summaries and up to 30 recent server-calendar days on compatible CLI versions. |
| **Codex usage history** | Browse retained quota observations and export CSV or JSON files. |
| **Codex task usage and earned resets** | Request server usage estimates for a task ID or use an existing earned reset on compatible CLI versions. |

Task estimates are not invoices or conversions of quota percentages. **Using an earned reset always requires confirmation and never purchases a reset.** If the outcome is unknown, retrying reuses the saved request ID, including after a restart.

The [user guide](USER_GUIDE.md) explains availability, exports, retention, and reset recovery.

## Troubleshooting

| Problem | What to do |
| --- | --- |
| Extension not visible | Confirm PowerToys meets the requirements and Command Palette is running. Run **Reload Command Palette Extension**, then check **Settings > Extensions > Codex Usage**. |
| Only fallback data, or no usage | Check the standalone CLI requirement above and confirm Codex is signed in. Restart PowerToys after changing the CLI path. Start Codex once if local account and session metadata are missing. |
| Old values or **Last confirmed** | Check the measurement time and latest refresh attempt in diagnostics, then retry a refresh. |
| Forecast unavailable | Allow fresh, continuous measurements to accumulate. Projections pause after gaps, stale data, or allowance increases until enough usage is observed. |
| Dock items missing after changing modes | Open Dock customization and add the bands for the selected combined or separate mode. |
| Settings not saved | Read the error on the settings page and retry saving. Until saving succeeds, changes apply only to the running session. |

Open **Codex Usage diagnostics** for the running build, source, measurement time, and latest refresh attempt. **Copy diagnostics** excludes account identifiers, paths, and raw service errors. Include this report when [reporting an issue](https://github.com/TheBeems/CodexUsageDock/issues).

## Privacy

The extension processes usage locally through the Codex CLI app-server and local session metadata. It has no developer-operated telemetry service and does not retain conversation content for token analysis. Codex's own service communication is governed by its policies.

Local storage includes settings, up to seven days of weekly trend data, and up to eight learned forecast profiles. Optional usage history retains 7, 30, or 90 days. Exported files remain after retained history is deleted. Account activity and task estimates stay in memory; pending earned-reset requests use a local recovery record.

See the [Privacy Policy](PRIVACY.md) for full storage and communication details.

## Development and license

See [DEVELOPMENT.md](DEVELOPMENT.md) for building, testing, and packaging, and the [Changelog](CHANGELOG.md) for release history. Codex Usage Dock is available under the [MIT License](LICENSE).
