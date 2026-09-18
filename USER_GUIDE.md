# User guide

For installation and first use, see the [README](README.md#quick-start). This guide explains the displays, optional features, and data handling in more detail.

[Charts and forecasts](#charts-and-forecasts) · [Dock settings](#dock-settings) · [Account activity](#account-activity) · [History and exports](#history-and-exports) · [Task usage and earned resets](#task-usage-and-earned-resets) · [Data sources and reliability](#data-sources-and-reliability) · [Upgrading from older versions](#upgrading-from-older-versions)

## Charts and forecasts

The dashboard groups Codex and additional quota categories such as Spark into the same compact windows. Each reported window shows **Remaining**, **Time elapsed**, and a local reset date. Blue shows remaining allowance, falling from 100% toward 0%; gray shows elapsed time, rising toward 100%. Large percentages also mean remaining allowance. Because these bars move in opposite directions, equal lengths do not indicate a sustainable pace. Missing or expired windows take one short line, without fabricated zero values. An unavailable time comparison is shown as **—**. Fresh low allowance is highlighted by the percentage.

The weekly chart shows **remaining allowance**, with 100% at the top and 0% at the bottom. Its solid line falls as quota is consumed and breaks at measurement gaps and allowance restorations; a restoration increases remaining allowance. Isolated measurements remain visible as points, and the latest observation is labeled. No earlier 100% observation is invented. A forecast reaching zero ends at its estimated limit time. Its label uses the same rounded estimate as the dashboard and information page; the plotted point retains the calculated time. Amber markers identify detected restorations. The chart uses a fixed dark background and brighter font outlines for readable axes in either host theme. Dashboard, information, and text-view dates use English names and local time; Dock-band subtitles retain regional formatting.

Equal-width local calendar-day columns retain dated weekday labels and partial reset-boundary days. Local daily token bars and their independent right-hand scale appear by default when available; a short legend distinguishes the two axes and identifies partial data. The weekly forecast status appears at the top, followed by Codex quotas and the weekly chart, then additional quota categories and reset credits. A single reported window uses the full width with a combined model/window heading and bars scaled to match paired windows; unused windows retain both bars with smaller percentages. **Usage information**, in the dashboard's **More** menu, opens a regular page with forecast explanations, the daily/hourly budget, reset-credit expirations, credits, source information, and restoration history. It omits repeated quota rows and absent forecast sections and includes a short chart guide. There is no Details toggle or dependency between that information page and the token chart.

Weekly projections require at least 30 minutes of fresh, continuous measurements. Recent pace uses at most the last six hours of that segment, so an old idle period cannot dilute the estimate indefinitely. With insufficient learned history, the projection explicitly describes what would happen if that recent pace continued. Its status identifies the measurement duration. The five-hour projection retains its shorter observation requirement.

The optional adaptive weekly forecast uses up to eight completed local quota cycles. A usable cycle must cover at least half of the week and include at least six observed hours in each of the four six-hour positions within a day. Missing observations are unknown, not zero consumption. Profiles older than eight weeks before the current window are excluded. Better coverage and more recent history allow the projection to transition faster from recent pace to a weighted historical median. A short burst therefore has less influence far into the future. The status shows usable weeks and their observed coverage, rather than just the number of stored cycles. The active cycle is not counted again as independent history.

Six-hour patterns require at least three usable weeks with at least three observed hours in the relevant bucket. These stored patterns are relative to the reset: they are used only when the local reset weekday, clock time, and UTC offset match the current window. A shifted reset or daylight-saving offset change falls back to the representative weekly average. No fixed workday or weekend schedule is assumed. The dashed projection changes at hourly steps and six-hour boundaries.

A segment starts again after an allowance increase or a gap longer than three times the freshness allowance (the greater of five minutes and the refresh interval). Text, chart, and alert projections pause until enough fresh measurements are available. Without usable history, a meaningful decrease is also required; with usable history, observed idle time can still produce an estimate. Weekly warning notifications follow the same adaptive setting and calculation as the dashboard.

Projections are conditional estimates, not calibrated probabilities. Estimated exhaustion at least a day away shows a date; nearer estimates are rounded up to a quarter hour. The chart retains the calculated points. The budget under Usage information shows the average percentage points per day (or hour near reset) available to last until reset, even while a forecast is pending. Remaining/time bars show allowance and elapsed window time; neither they nor the budget guarantee future consumption. The dashboard therefore omits reassuring pace labels such as “On track”. The model is tested with deterministic scenarios and chronological synthetic benchmarks; its accuracy on your actual future weeks has not been established.

The extension also reads aggregate `token_count` records from active and archived local Codex session logs to show locally observed total tokens per calendar day. Limits update first; token bars update independently when local analysis finishes. These token bars are local activity observations, not an exact accounting of allowance consumption. The Usage information page identifies the allowance source: the live route is explicitly the CLI app-server; session metadata may have been written by the desktop app, CLI, or another local Codex client and cannot be attributed more precisely.

### Text alternative

**Codex usage in text**, available from the extension command list, groups quotas with the same model and window names as the dashboard. A default quota appears once even when its API position differs from its duration. Short text rows use the available width for reset times, recent measured weekly points, and full local daily token totals, avoiding narrow table cells. Times are local, with the time zone identified; history exports continue to use UTC. Missing values, reported zero, expired windows, and last-confirmed observations have distinct text labels.

## Dock settings

Open Command Palette and select **Codex Usage settings**.

**Compact Dock** shortens quota labels to forms such as `5h47%` and `W86%` and hides reset times while retaining stale/source warnings. **Separate Dock items** offers each visible metric as a separate pinnable band. Turning it off offers the combined band. Pins belonging to the inactive mode and hidden metrics stop displaying items and are not restored as active bands after a reload. Command Palette keeps its saved pins: switching modes does not move or convert them. Add the desired bands through Dock customization if they were not already pinned; switching back makes matching saved pins available again.

### Usage alerts

**Enable usage alerts** is off by default. When enabled, fresh, identified account data can notify on a downward crossing of 10% remaining, a new projected limit within one hour, or a reset credit entering its last 24 hours. The first measurement establishes a baseline. Duplicate refreshes do not repeat alerts, small reset-time fluctuations stay in the same cycle, and account/category changes start a new baseline. Multiple simultaneous alerts are combined into one host notification. Delivery depends on the Command Palette host.

### Forecast learning

You can enable or pause the adaptive weekly forecast. Pausing keeps its learned local history and excludes measurements collected while paused. **Delete learned forecast history** asks for confirmation before permanently clearing it.

### Saving settings

The extension saves these choices in `CodexUsageDock/settings.json` under the current user's Windows local application data directory and restores them before refreshing after a restart. If saving fails, the page explains that the choices apply only to the running session and lets you save again. Deleting learned history confirms success only after the cleared state is saved. History read/write failures also appear in Usage information.

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

The dashboard renders separate quota categories returned by newer Codex versions with the same Remaining/time bars, including durations other than five hours or one week. The familiar Dock entries and weekly forecast describe only the default category. Percentages from different categories are never added or averaged. Available reset credits have a compact count; an expiry within 24 hours is highlighted. The full credits balance is under Usage information, and credits remain usable even when there are no percentage windows.

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
