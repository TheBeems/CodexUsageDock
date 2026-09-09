# Privacy Policy

Last updated: September 9, 2026

Codex Usage Dock is a local Windows extension for PowerToys Command Palette. It displays Codex usage limits, earned resets, reset expiry times, and available credits in the Command Palette Dock.

## Data accessed

The extension accesses the locally installed Codex app-server to request account rate-limit information. If that request is unavailable, it may read local Codex session metadata as a fallback. It also reads aggregate `token_count` records from active and archived local Codex session logs to total locally observed tokens by calendar day. It does not retain or display prompts, responses, tool output, thread names, or session source paths for this analysis. This data is used only to render the usage indicators on the user's device.

## Data collection and transmission

Codex Usage Dock does not collect, sell, share, or transmit personal information, usage information, or session metadata to the developer or to any developer-operated service. It does not include analytics, advertising, telemetry, or tracking.

Communication initiated by the extension is limited to the local Codex app-server. Codex and any services it communicates with are governed by their own terms and privacy policies.

## Data storage

Settings are saved explicitly in `CodexUsageDock/settings.json` under the current Windows user's local application data directory, alongside the local history files. This file contains only display, refresh, and forecasting preferences. Failed writes are reported without logging file contents, personal paths, or credentials. Learned-history deletion is confirmed only after its empty state has been saved successfully.

The extension does not create an external user account or remote database. Settings and temporary runtime state remain on the user's Windows device. Daily token totals and per-file read positions are kept only in memory and are rebuilt from the current weekly window after a restart. To keep the weekly usage trend available after Command Palette restarts, it stores a rolling maximum of seven days of local timestamps and remaining weekly-percentage measurements. When the adaptive weekly forecast is enabled, it also stores at most eight aggregated quota-cycle profiles: total observed duration and consumption, plus six-hour usage buckets relative to the reset. These files contain no account, session, prompt, or message content and are never transmitted. Users can pause learning while keeping those profiles; measurements collected while paused are not added later. Users can also delete the learned profiles from Codex Usage settings.

Account-scoped history uses a one-way hash of the account identity supplied by Codex, combined with the default quota category, as an opaque local directory name. Raw account identifiers and email addresses are not stored or shown in diagnostics. Legacy history without account attribution is not imported into a verified account; unverified observations remain in memory and do not train saved forecasts. The last confirmed usage snapshot is retained only in memory during an outage, with its original timestamp. Diagnostics exposes field availability and bounded status messages, not raw service errors, credentials, or personal paths.

Optional source preferences store the user-selected executable and Codex home paths locally in settings. These paths are not included in diagnostic reports or logs. Account activity requests travel through the local Codex app-server and retain only aggregate daily token counts and optional totals in memory. Identity is checked before and after each request. Optional usage notifications contain a quota label and bounded status text, without account identifiers; their deduplication state remains in memory. Disabling account activity clears the visible account activity state and stops new optional reads.

## Permissions

The Windows `runFullTrust` capability is required to run the packaged Command Palette COM server and to communicate with the locally installed Codex process. It is not used to bypass Windows security controls or access unrelated user data.

## Contact

Questions or privacy requests can be submitted through the public issue tracker:

https://github.com/TheBeems/CodexUsageDock/issues
