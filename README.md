# Codex Usage Dock

Codex Usage Dock is a Windows Command Palette extension that shows your Codex limits directly in the PowerToys Dock.

It displays:

- the percentage remaining in the rolling five-hour usage window;
- the percentage remaining in the weekly usage window;
- compact pace indicators that compare allowance used with elapsed window time;
- a projected allowance at reset, or an estimated limit time when current consumption would exhaust it sooner;
- an optional adaptive weekly forecast that keeps the current pace dominant and gradually blends in up to eight local quota cycles and six-hour usage patterns;
- a weekly trend chart with sampled remaining allowance on a 0–100% scale, a dashed projection that can follow six-hour adaptive forecast points, and locally observed total-token bars on an independent daily scale; equal-width local calendar-day columns retain dated weekday labels and partial reset-boundary days, the line connects samples across measurement gaps but breaks at allowance increases, amber markers identify detected allowance restorations, and the forecast uses only the latest post-restoration segment;
- a textual summary of the latest detected weekly allowance restoration, plus the current window's restoration history in the Details pane;
- the number of available earned resets and their expiry times;
- the remaining credits balance when Codex provides it.

The values refresh once per minute by default, with 5- and 15-minute intervals available in settings. The extension reads live data from the standalone Codex CLI app-server and uses local Codex session metadata as a fallback. Fallback selection uses the quota event's own timestamp, and the Dock shows its age. Local session readers use `CODEX_HOME` when set, otherwise the current user's `.codex` directory. The overall status identifies the most restrictive active quota window.

The extension also reads aggregate `token_count` records from active and archived local Codex session logs to show locally observed total tokens per calendar day. Limits update first; token bars update independently when local analysis finishes. These token bars are local activity observations, not an exact accounting of allowance consumption. The details page identifies the allowance source: the live route is explicitly the CLI app-server; session metadata may have been written by the desktop app, CLI, or another local Codex client and cannot be attributed more precisely.

Forecasts use the average consumption rate since the beginning of the latest continuous segment. A segment starts again after an allowance increase or a gap longer than three times the freshness allowance (the greater of five minutes and the refresh interval). Text and chart projections pause until enough fresh, continuous measurements show a meaningful decrease. The solid observed line still connects sampled values across gaps.

## Requirements

- Windows 10 build 19041 or newer, or Windows 11
- [PowerToys 0.100.0 or newer](https://github.com/microsoft/PowerToys) with Command Palette enabled
- Codex installed and signed in
- A standalone `codex.exe` or `codex.cmd` CLI available on `PATH`, or `CODEX_USAGE_DOCK_CODEX_PATH` set to its full path. The protected CLI bundled with the Microsoft Store Codex Desktop app cannot be launched by this extension.

The package includes the required .NET runtime. You do not need the .NET SDK to install or use the extension.

## Install

Microsoft Store is the supported production installer. Install [Codex Usage Dock from the Microsoft Store](https://apps.microsoft.com/detail/9NFCPJXQG9FG).

GitHub Actions artifacts are inputs for Microsoft Store certification, not public installers. Do not distribute or install them directly.

Codex Usage Dock is activated inside PowerToys Command Palette and intentionally has no standalone Start-menu entry.

## Add Codex Usage to the Dock

1. Open Command Palette settings.
2. Select **Extensions** and make sure **Codex Usage** is enabled.
3. Select **Dock (Preview)** and enable the Dock.
4. Open the Dock customization interface.
5. Choose **Add command** (`+`) in the section where you want the widget.
6. Search for **Codex Usage** and select its Dock band.

The Dock will show entries similar to `5h 47%`, `Week 86%`, and `2 resets · 10.00`. The percentages represent the amount remaining. The final entry shows available earned resets, the time until the next reset credit expires in whole hours or days, and, when available, the credits balance. Select an entry to see reset expiry details or refresh the data manually.

## Customize the Dock

**Compact Dock** shortens quota labels to forms such as `5h47%` and `W86%` and hides reset times while retaining stale/source warnings. **Separate Dock items** offers each metric as a separate pinnable band; existing combined-band and individual pin identifiers remain resolvable after changing modes.

**Enable usage alerts** is off by default. When enabled, fresh, identified account data can notify on a downward crossing of 10% remaining, a new projected limit within one hour, or a reset credit entering its last 24 hours. The first measurement establishes a baseline. Duplicate refreshes do not repeat alerts, small reset-time fluctuations stay in the same cycle, and account/category changes start a new baseline. Multiple simultaneous alerts are combined into one host notification. Delivery depends on the Command Palette host.

Open Command Palette and select **Codex Usage settings** to choose which usage entries appear in the Dock. You can independently show or hide the five-hour limit, weekly limit, and resets and credits, choose whether usage entries show their reset time, set the local data refresh interval to 1, 5, or 15 minutes, and enable or pause the adaptive weekly forecast. Pausing the forecast keeps its learned local history and excludes measurements collected while it is paused; **Delete learned forecast history** asks for confirmation before permanently clearing it.

The extension saves these choices in `CodexUsageDock/settings.json` under the current user's Windows local application data directory and restores them before refreshing after a restart. If saving fails, the page explains that the choices apply only to the running session and lets you save again. Deleting learned history confirms success only after the cleared state is saved. History read/write failures also appear in Details. Choices lost by older versions cannot be recovered; set them once again after updating.

## Update

Microsoft Store installs updates automatically. You can also check for updates from **Microsoft Store > Library**.

## Sources and account activity

Settings accepts an optional full path to a standalone `codex.exe` or `codex.cmd` and an optional Codex home directory. Empty fields retain environment-based discovery. An explicit directory can be a Windows-accessible WSL path; the extension reads that directory and passes it to the Windows CLI as `CODEX_HOME`, without starting WSL or changing Codex configuration. Inaccessible or invalid explicit paths stop source reads and show a settings error. A profile change clears the displayed context and discards results from the previous in-flight read.

**Codex account activity** shows account-wide token summaries and up to 30 recent server-calendar days when `account/usage/read` is supported. It updates independently after quota data, at most every five minutes automatically; unsupported versions retry after 30 minutes. **Refresh now** on that page requests an immediate retry. Disable **Show account activity** to stop these optional reads. Account identity must match before and after the request. Missing days and fields are not zero usage, and the server's unspecified calendar time zone is kept separate from local calendar-day chart bars. No account activity is written to disk by this feature.

**Codex source profiles** saves up to eight named sets of executable and home paths. Add a profile, then choose **Use profile** to apply it and persist it for the next start. Reusing a name replaces that preset. Profiles contain no copied credentials; a name does not verify the signed-in account. Saved WSL or network directories can remain listed while offline, but paths must be accessible before use. Deleting a preset asks for confirmation and leaves the active source settings unchanged. Only one Codex source is active at a time.

**Codex usage in text**, also available from Details, provides quota tables, reset times, recent measured weekly points, and local daily token totals without relying on charts or color. Missing values, reported zero, expired windows, and last-confirmed observations have distinct text labels.

## Optional Claude usage pilot

The pilot displays separate Claude five-hour and seven-day limits from an explicitly selected local capture file. It is off by default and adds its own Dock band when enabled. It does not verify the Claude account, combine Claude percentages with Codex, or infer costs. Claude reads run independently of a slow Codex refresh.

The bridge uses Claude Code's documented `rate_limits.five_hour` and `rate_limits.seven_day` statusline fields. These may be absent independently, appear only after a session receives an API response, and require an eligible subscription. The pilot does not support gateway spend-limit fields. See the [official statusline field documentation](https://code.claude.com/docs/en/statusline#available-data).

1. Copy [capture-claude-usage.ps1](scripts/capture-claude-usage.ps1) from this repository to a permanent location you control. The companion script is not bundled into the MSIX application.
2. Configure the command in your Claude statusline settings using the official instructions. If you have no existing formatter, use the script's **standalone** mode; replace both example paths with your own absolute paths:

   ```text
   powershell.exe -NoProfile -NonInteractive -File "C:/Tools/capture-claude-usage.ps1" -OutputPath "C:/UsageCaptures/claude-usage.json" -Standalone
   ```

   Standalone mode displays a compact remaining-quota line. To retain an existing formatter, omit `-Standalone`, launch the capture script as a separate PowerShell process, and pipe that process's stdout into your existing formatter command. Default mode forwards the original stdin bytes unchanged, including when the capture destination fails. Calling the script inside the same PowerShell process is not a supported pipeline arrangement. The extension does not edit your Claude settings or replace a statusline automatically.
3. In **Codex Usage settings**, set **Claude bridge file** to the same absolute JSON file path and turn on **Enable Claude usage pilot**. The script creates the output directory when needed.
4. Open **Claude usage pilot** to inspect capture status, observation time, and each independent window. Add its Claude Dock band through Dock customization.

The bridge retains at most 256 KiB of input for parsing and writes only the schema, provider, UTC capture time, validated quota windows, and generic availability messages. Writes replace the snapshot atomically. Missing, malformed, or oversized input writes an unavailable snapshot; it never refreshes the timestamp on old quota values. The extension reads at most 64 KiB per capture. Stale, future-dated, missing, and expired data do not appear as available Dock quota. **Refresh captures** rereads the file; it does not make Claude emit new data. After a quiet session, wait for a new Claude statusline update.

## History, planning, and optional account actions

**Codex usage history** retains quota observations only when **Retain usage observations** is set to 7, 30, or 90 days. Observations are scoped to the identified account and default quota category, sampled in five-minute buckets, and capped at 27,000 rows. Reset changes within a bucket remain separate observations. Pausing collection keeps retained data; the history page offers confirmed deletion for the selected context. CSV and JSON export actions write files to the extension's local application data `exports` folder and show the resulting path. Exports contain quota percentages and UTC observation/reset times, without account IDs or conversation content. Exported copies are not deleted when retained history is cleared.

**Codex workday planner** uses today's local **Workday end** and your chosen **Workdays remaining before weekly reset**. It divides remaining weekly allowance across those days and today's remaining hours; the five-hour allowance is budgeted independently. Planning stops at an earlier reset and pauses after today's chosen end. It requires fresh, identified live data. The page explains the measurement span, continuous segment, learned-cycle count, and a held-out latest-observation check where sufficient data exists. This is descriptive evidence, not a calibrated confidence percentage or a guarantee. Its recent-pace estimate is separate from the dashboard's optional adaptive weekly forecast.

**Codex task usage and earned resets** accepts an explicit task ID for `account/usage/read` on compatible CLI versions. It shows server-estimated credits and optional USD, plus model/effort/speed and available input/cached/output token groups. These estimates are not invoices or conversions of quota percentages. Task reads verify account identity before and after, keep the most recently requested task, and retain results only in memory.

The same page offers **Use or retry an earned reset**, which always asks for confirmation. It only uses an existing earned reset, never purchases one, and verifies the expected account before the mutation. A request ID is saved locally before sending. Unknown outcomes retain that exact ID across retries and extension restarts; concurrent clicks share the same attempt. An unreadable or unwritable recovery record stops the request. Only an unambiguous server outcome clears the pending record, and limits are refreshed afterward. This feature has synthetic protocol and service tests; no real credit was consumed while developing it.

## Uninstall

Remove **Codex Usage Dock** from **Windows Settings > Apps > Installed apps**.

## Troubleshooting

- Open **Codex Usage diagnostics** for the running build, measurement time, latest refresh attempt, source, and reset-field availability. **Copy diagnostics** copies only the safe report, without account identifiers, paths, or raw service errors. A missing reset count is different from an explicitly reported zero.
- Confirm that a standalone `codex.exe` or `codex.cmd` is available on `PATH`, or set `CODEX_USAGE_DOCK_CODEX_PATH` to its full path and restart PowerToys. The extension will show local fallback data when no launchable CLI is found.
- Confirm that Codex is signed in.
- Confirm that PowerToys Command Palette is enabled and running.
- Confirm that PowerToys is version 0.100.0 or newer.
- If the extension does not appear, open Command Palette, run **Reload Command Palette Extension**, and then confirm that **Codex Usage** is enabled under **Settings > Extensions**.
- If usage cannot be loaded, start Codex once so local account and session metadata are available.

## Distribution status

Microsoft Store product `9NFCPJXQG9FG` is the only production and update channel. GitHub releases identify source versions; a source release does not establish Store rollout or the version installed on a device. Check Microsoft Store for available updates and Windows Apps settings for the installed package version. Diagnostics reports the running extension build separately.

## Privacy

The extension runs locally. It talks to the standalone Codex CLI app-server and may read local Codex session metadata for its fallback path and aggregate token counters for the daily chart bars. It does not retain prompts, responses, tool output, thread names, or source paths for token analysis. It keeps a rolling seven-day weekly usage trend and up to eight aggregated local weekly forecast profiles on the device, and does not send usage information to a separate service.

See the full [Privacy Policy](PRIVACY.md).

## Data reliability

Details retains separate quota categories returned by newer Codex versions, including durations other than five hours or one week. The familiar Dock entries and weekly forecast describe only the default category. Percentages from different categories are never added or averaged. Credits remain usable even when there are no percentage windows.

Freshness uses the greater of five minutes and the configured refresh interval throughout the UI and forecasts. After a failed refresh, a previous live measurement can remain visible as **Last confirmed** with its original timestamp; projections and new history learning pause. An unverified session log cannot replace a confirmed account measurement. At first launch, local session fallback is still available if live data cannot be obtained.

When Codex supplies an account identity, weekly history and learned profiles are stored separately for that account and default quota category using opaque hashed directory names. History appears only after the account is identified. Older history files have no identity and are not imported into an account. Without a verified account identity, recent observations remain in memory and adaptive learning is paused.

The local quota fallback caches read positions and the latest valid quota event in memory. Unchanged files still in its cache are not reread for content; appended, replaced, truncated, and deleted files are handled on later refreshes. Each scan reads at most 8 MiB of file content, tracks up to 512 files, and bounds partial lines to 128 KiB. It still enumerates session file metadata; these limits do not promise constant scan time for large directories. Incomplete scans are reported, and later refreshes continue discovery. No session payload or read-position cache is saved to disk by this quota fallback.

## Development

Build, test, Store packaging, and release instructions are in [DEVELOPMENT.md](DEVELOPMENT.md).

## License

Codex Usage Dock is available under the [MIT License](LICENSE).
