# Privacy Policy

Last updated: September 9, 2026

Codex Usage Dock is a local Windows extension for PowerToys Command Palette. It displays Codex usage limits, earned resets, reset expiry times, and available credits in the Command Palette Dock.

## Data accessed

The extension accesses the locally installed Codex app-server to request account rate-limit information. If that request is unavailable, it may read local Codex session metadata as a fallback. It also reads aggregate `token_count` records from active and archived local Codex session logs to total locally observed tokens by calendar day. It does not retain or display prompts, responses, tool output, thread names, or session source paths for this analysis. This data is used only to render the usage indicators on the user's device.

## Data collection and transmission

Codex Usage Dock does not collect, sell, share, or transmit personal information, usage information, or session metadata to the developer or to any developer-operated service. It does not include analytics, advertising, telemetry, or tracking.

Communication initiated by the extension is limited to the local Codex app-server. Codex and any services it communicates with are governed by their own terms and privacy policies.

## Data storage

Settings are saved explicitly in `CodexUsageDock/settings.json` under the current Windows user's local application data directory, alongside the local history files. This file contains display, refresh, planning, retention, forecasting, and explicit source preferences. Failed writes are reported without logging file contents, personal paths, or credentials. Learned-history deletion is confirmed only after its empty state has been saved successfully.

The extension does not create an external user account or remote database. Settings and temporary runtime state remain on the user's Windows device. Daily token totals and per-file read positions are kept only in memory and are rebuilt from the current weekly window after a restart. To keep the weekly usage trend available after Command Palette restarts, it stores a rolling maximum of seven days of local timestamps and remaining weekly-percentage measurements. When the adaptive weekly forecast is enabled, it also stores at most eight aggregated quota-cycle profiles: total observed duration and consumption, plus six-hour usage buckets relative to the reset. These files contain no account, session, prompt, or message content and are never transmitted. Users can pause learning while keeping those profiles; measurements collected while paused are not added later. Users can also delete the learned profiles from Codex Usage settings.

Account-scoped history uses a one-way hash of the account identity supplied by Codex, combined with the default quota category, as an opaque local directory name. Raw account identifiers and email addresses are not stored or shown in diagnostics. Legacy history without account attribution is not imported into a verified account; unverified observations remain in memory and do not train saved forecasts. The last confirmed usage snapshot is retained only in memory during an outage, with its original timestamp. Diagnostics exposes field availability and bounded status messages, not raw service errors, credentials, or personal paths.

Optional source preferences store the user-selected executable and Codex home paths locally in settings. These paths are not included in diagnostic reports or logs. Account activity requests travel through the local Codex app-server and retain only aggregate daily token counts and optional totals in memory. Identity is checked before and after each request. Optional usage notifications contain a quota label and bounded status text, without account identifiers; their deduplication state remains in memory. Disabling account activity clears the visible account activity state and stops new optional reads.

Optional retained history stores at most 27,000 aggregate quota observations with UTC timestamps and reset times, separated by hashed account/category context. The selected retention is 7, 30, or 90 days; collection starts only after opting in. Explicit CSV/JSON exports create local files without account IDs or conversation content. Users control deletion of retained observations and exported copies separately. Neither retained observations nor exports contain prompts or task contents.

An explicitly requested task-usage read passes the user-entered task ID through the local Codex app-server and keeps the resulting aggregate estimates only in memory. A confirmed earned-reset action sends a mutation through that server. Before sending, the extension stores a random request ID in the account's hashed local context. An unresolved ID is retained across restarts so a retry cannot accidentally become a separate redemption. Recovery records contain no authentication credentials or raw account identifiers and are separate from history deletion.

Optional source profiles store up to eight display names and executable/home paths in the local `profiles.json` beside settings. Profile selection changes only the extension's source preferences; it does not copy authentication or alter Codex configuration. Deleting a preset does not delete a source directory or change the active source. The quota fallback keeps bounded file metadata, read positions, partial lines, and its latest parsed quota event only in memory.

The optional Claude pilot reads only the capture file explicitly selected in settings. Its separately configured companion script receives Claude statusline input and retains at most 256 KiB in memory for parsing. Default mode forwards that input to the user's existing formatter; standalone mode emits only a compact quota line. The saved capture contains only a schema version, provider label, UTC timestamp, validated five-hour/seven-day usage percentages and reset times, and generic status messages. It does not copy workspace paths, model details, account identifiers, credentials, prompts, or responses into the capture. The extension does not request Claude credentials, contact Claude services, edit Claude configuration, or upload the capture. Users manage the script and its output file separately; disabling the pilot clears its displayed state and stops new reads but does not delete the file.

## Permissions

The Windows `runFullTrust` capability is required to run the packaged Command Palette COM server and to communicate with the locally installed Codex process. It is not used to bypass Windows security controls or access unrelated user data.

## Contact

Questions or privacy requests can be submitted through the public issue tracker:

https://github.com/TheBeems/CodexUsageDock/issues
