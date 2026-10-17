# Privacy Policy

Last updated: October 17, 2026

Codex Usage Dock is a local Windows extension for PowerToys Command Palette. It displays Codex usage limits, earned resets, reset expiry times, and available credits in the Command Palette Dock.

## Data accessed

The extension accesses the locally installed Codex app-server to request account rate-limit information. If that request is unavailable, it may read local Codex session metadata as a fallback. It also reads aggregate `token_count` records from active and archived local Codex session logs to total locally observed tokens by calendar day. It does not retain or display prompts, responses, tool output, thread names, or session source paths for this analysis. This data is used only to render the usage indicators on the user's device.

## Data collection and transmission

Codex Usage Dock does not collect, sell, share, or transmit personal information, usage information, or session metadata to the developer or to any developer-operated service. It does not include analytics, advertising, telemetry, or tracking.

Communication initiated by the extension is limited to the local Codex app-server. Codex and any services it communicates with are governed by their own terms and privacy policies.

## Data storage

Settings are saved explicitly in `CodexUsageDock/settings.json` under the current Windows user's local application data directory, alongside the local history files. This file contains display, refresh, retention, and forecasting preferences. Failed writes are reported without logging file contents, personal paths, or credentials. Learned-history deletion is confirmed only after its empty state has been saved successfully.

The extension does not create an external user account or remote database. Settings and temporary runtime state remain on the user's Windows device. Daily token totals and per-file read positions are kept only in memory and are rebuilt for the retained seven-day chart horizon after a restart, including observations before the latest quota reset. To keep the weekly usage trend available after Command Palette restarts, it stores a rolling maximum of seven days of local timestamps and remaining weekly-percentage measurements. Adaptive forecast learning stores up to 56 days plus the current partial hour of UTC-hour aggregates: observed duration and quota consumption, plus the last measurement needed to resume learning. The separate `adaptive-weekly-forecast.hourly.json` file is account/category scoped. Existing reset-relative forecast files are preserved unchanged and their valid learned summaries may supply historical startup estimates. These files contain no raw account identifiers, session content, prompts, or messages and are never transmitted. Pausing learning excludes measurements collected while paused, including across restarts. Confirmed deletion clears active hourly learning and disables reuse of preserved legacy profiles and old raw measurements; original history files are retained. An unreadable hourly file is not overwritten during refresh; confirmed deletion can reset it.

Account-scoped history uses a one-way hash of the account identity supplied by Codex, combined with the default quota category, as an opaque local directory name. Raw account identifiers and email addresses are not stored or shown in diagnostics. Legacy history without account attribution is not imported into a verified account; unverified observations remain in memory and do not train saved forecasts. The last confirmed usage snapshot is retained only in memory during an outage, with its original timestamp. Diagnostics exposes field availability and bounded status messages, not raw service errors, credentials, or personal paths.

Account activity requests travel through the local Codex app-server and retain only aggregate daily token counts and optional totals in memory. Identity is checked before and after each request. Optional usage notifications contain a quota label and bounded status text, without account identifiers; their deduplication state remains in memory. Disabling account activity clears the visible account activity state and stops new optional reads.

Optional retained history stores at most 27,000 aggregate quota observations with UTC timestamps and reset times, separated by hashed account/category context. The selected retention is 7, 30, or 90 days; collection starts only after opting in. Explicit CSV/JSON exports create local files without account IDs or conversation content. Users control deletion of retained observations and exported copies separately. Neither retained observations nor exports contain prompts or task contents.

An explicitly requested task-usage read passes the user-entered task ID through the local Codex app-server and keeps the resulting aggregate estimates only in memory. A confirmed earned-reset action sends a mutation through that server. Before sending, the extension stores a random request ID in the account's hashed local context. An unresolved ID is retained across restarts so a retry cannot accidentally become a separate redemption. Recovery records contain no authentication credentials or raw account identifiers and are separate from history deletion.

The quota fallback keeps bounded file metadata, read positions, partial lines, and its latest parsed quota event only in memory.

Source-path, source-label, and Claude preferences from earlier development builds are ignored and omitted on the next settings save. The extension no longer reads saved source-profile or Claude capture files. Existing profile files and externally configured capture scripts or files remain under the user's control and are not deleted or modified by the extension.

## Permissions

The Windows `runFullTrust` capability is required to run the packaged Command Palette COM server and to communicate with the locally installed Codex process. It is not used to bypass Windows security controls or access unrelated user data.

## Contact

Questions or privacy requests can be submitted through the public issue tracker:

https://github.com/TheBeems/CodexUsageDock/issues
