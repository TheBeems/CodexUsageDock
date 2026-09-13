# User guide

For installation and first use, see the [README](README.md#quick-start). This guide explains the displays, optional features, and data handling in more detail.

[Charts and forecasts](#charts-and-forecasts) · [Dock settings](#dock-settings) · [Account activity](#account-activity) · [History and exports](#history-and-exports) · [Task usage and earned resets](#task-usage-and-earned-resets) · [Data sources and reliability](#data-sources-and-reliability) · [Upgrading from older versions](#upgrading-from-older-versions)

## Charts and forecasts

The weekly chart shows sampled remaining allowance on a 0–100% scale. Its solid line connects samples across measurement gaps but breaks at allowance increases. Amber markers identify detected allowance restorations. The latest restoration is summarized below the chart, with the current window's restoration history in Details.

Equal-width local calendar-day columns retain dated weekday labels and partial reset-boundary days. Locally observed total-token bars use an independent daily scale.

The optional adaptive weekly forecast keeps the current pace dominant and gradually blends in up to eight local quota cycles and six-hour usage patterns. Its dashed projection can follow six-hour forecast points and uses only the latest post-restoration segment. Pace indicators compare allowance used with elapsed window time. Projections estimate allowance remaining at reset, or the time the limit would be reached if consumption would exhaust it sooner.

Forecasts use the average consumption rate since the beginning of the latest continuous segment. A segment starts again after an allowance increase or a gap longer than three times the freshness allowance (the greater of five minutes and the refresh interval). Text and chart projections pause until enough fresh, continuous measurements show a meaningful decrease. The solid observed line still connects sampled values across gaps.

The extension also reads aggregate `token_count` records from active and archived local Codex session logs to show locally observed total tokens per calendar day. Limits update first; token bars update independently when local analysis finishes. These token bars are local activity observations, not an exact accounting of allowance consumption. The details page identifies the allowance source: the live route is explicitly the CLI app-server; session metadata may have been written by the desktop app, CLI, or another local Codex client and cannot be attributed more precisely.

### Text alternative

**Codex usage in text**, also available from Details, provides quota tables, reset times, recent measured weekly points, and local daily token totals without relying on charts or color. Missing values, reported zero, expired windows, and last-confirmed observations have distinct text labels.

## Dock settings

Open Command Palette and select **Codex Usage settings**.

**Compact Dock** shortens quota labels to forms such as `5h47%` and `W86%` and hides reset times while retaining stale/source warnings. **Separate Dock items** offers each visible metric as a separate pinnable band. Turning it off offers the combined band. Pins belonging to the inactive mode and hidden metrics stop displaying items and are not restored as active bands after a reload. Command Palette keeps its saved pins: switching modes does not move or convert them. Add the desired bands through Dock customization if they were not already pinned; switching back makes matching saved pins available again.

### Usage alerts

**Enable usage alerts** is off by default. When enabled, fresh, identified account data can notify on a downward crossing of 10% remaining, a new projected limit within one hour, or a reset credit entering its last 24 hours. The first measurement establishes a baseline. Duplicate refreshes do not repeat alerts, small reset-time fluctuations stay in the same cycle, and account/category changes start a new baseline. Multiple simultaneous alerts are combined into one host notification. Delivery depends on the Command Palette host.

### Forecast learning

You can enable or pause the adaptive weekly forecast. Pausing keeps its learned local history and excludes measurements collected while paused. **Delete learned forecast history** asks for confirmation before permanently clearing it.

### Saving settings

The extension saves these choices in `CodexUsageDock/settings.json` under the current user's Windows local application data directory and restores them before refreshing after a restart. If saving fails, the page explains that the choices apply only to the running session and lets you save again. Deleting learned history confirms success only after the cleared state is saved. History read/write failures also appear in Details.

## Account activity

**Codex account activity** shows account-wide token summaries and up to 30 recent server-calendar days when `account/usage/read` is supported. It updates independently after quota data, at most every five minutes automatically; unsupported versions retry after 30 minutes. **Refresh now** on that page requests an immediate retry. Disable **Show account activity** to stop these optional reads. Account identity must match before and after the request. Missing days and fields are not zero usage, and the server's unspecified calendar time zone is kept separate from local calendar-day chart bars. No account activity is written to disk by this feature.

## History and exports

**Codex usage history** retains quota observations only when **Retain usage observations** is set to 7, 30, or 90 days. Observations are scoped to the identified account and default quota category, sampled in five-minute buckets, and capped at 27,000 rows. Reset changes within a bucket remain separate observations. Pausing collection keeps retained data; the history page offers confirmed deletion for the selected context. CSV and JSON export actions write files to the extension's local application data `exports` folder and show the resulting path. Exports contain quota percentages and UTC observation/reset times, without account IDs or conversation content. Exported copies are not deleted when retained history is cleared.

## Task usage and earned resets

**Codex task usage and earned resets** accepts an explicit task ID for `account/usage/read` on compatible CLI versions. It shows server-estimated credits and optional USD, plus model/effort/speed and available input/cached/output token groups. These estimates are not invoices or conversions of quota percentages. Task reads verify account identity before and after, keep the most recently requested task, and retain results only in memory.

### Using an earned reset

The same page offers **Use or retry an earned reset**, which always asks for confirmation. It only uses an existing earned reset, never purchases one, and verifies the expected account before the mutation. A request ID is saved locally before sending. Unknown outcomes retain that exact ID across retries and extension restarts; concurrent clicks share the same attempt. An unreadable or unwritable recovery record stops the request. Only an unambiguous server outcome clears the pending record, and limits are refreshed afterward. This feature has synthetic protocol and service tests; no real credit was consumed while developing it.

## Data sources and reliability

### Live data and local fallback

The values refresh once per minute by default, with 5- and 15-minute intervals available in settings. The extension reads live data from the standalone Codex CLI app-server and uses local Codex session metadata as a fallback. Fallback selection uses the quota event's own timestamp, and the Dock shows its age. Local session readers use `CODEX_HOME` when set, otherwise the current user's `.codex` directory. The overall status identifies the most restrictive active quota window.

Codex is detected using the environment configuration described in [Requirements](README.md#requirements). The extension has no path fields or source-profile selection in its settings.

### Quota categories

Details retains separate quota categories returned by newer Codex versions, including durations other than five hours or one week. The familiar Dock entries and weekly forecast describe only the default category. Percentages from different categories are never added or averaged. Credits remain usable even when there are no percentage windows.

### Freshness and account isolation

Freshness uses the greater of five minutes and the configured refresh interval throughout the UI and forecasts. After a failed refresh, a previous live measurement can remain visible as **Last confirmed** with its original timestamp; projections and new history learning pause. An unverified session log cannot replace a confirmed account measurement. At first launch, local session fallback is still available if live data cannot be obtained.

When Codex supplies an account identity, weekly history and learned profiles are stored separately for that account and default quota category using opaque hashed directory names. History appears only after the account is identified. Older history files have no identity and are not imported into an account. Without a verified account identity, recent observations remain in memory and adaptive learning is paused.

### Local fallback scan limits

The local quota fallback caches read positions and the latest valid quota event in memory. Unchanged files still in its cache are not reread for content; appended, replaced, truncated, and deleted files are handled on later refreshes. Each scan reads at most 8 MiB of file content, tracks up to 512 files, and bounds partial lines to 128 KiB. It still enumerates session file metadata and skips inaccessible subdirectories; these limits do not promise constant scan time for large directories. Incomplete scans are reported, and later refreshes continue discovery. No session payload or read-position cache is saved to disk by this quota fallback.

For local storage, retention, and communication details, see the [Privacy Policy](PRIVACY.md).

## Upgrading from older versions

Saved source paths, source labels, and Claude preferences from earlier development builds are ignored. Other Codex preferences are preserved, and obsolete fields are omitted the next time settings are saved. Old source-profile files and externally configured capture scripts or files are not deleted or modified by the extension.

The workday planner and its end-time and remaining-workdays settings have been removed. Forecasts use observed usage without requiring a work schedule. Saved planner preferences from earlier development builds are ignored and omitted the next time settings are saved; other preferences and usage history are preserved.

Choices lost by older versions cannot be recovered; set them once again after updating. See the [Changelog](CHANGELOG.md) for version-specific changes.
