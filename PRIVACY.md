# Privacy Policy

Last updated: July 19, 2026

Codex Usage Dock is a local Windows extension for PowerToys Command Palette. It displays Codex usage limits, earned resets, reset expiry times, and available credits in the Command Palette Dock.

## Data accessed

The extension accesses the locally installed Codex app-server to request account rate-limit information. If that request is unavailable, it may read local Codex session metadata as a fallback. It also reads aggregate `token_count` records from active and archived local Codex session logs to total locally observed tokens by calendar day. It does not retain or display prompts, responses, tool output, thread names, or session source paths for this analysis. This data is used only to render the usage indicators on the user's device.

## Data collection and transmission

Codex Usage Dock does not collect, sell, share, or transmit personal information, usage information, or session metadata to the developer or to any developer-operated service. It does not include analytics, advertising, telemetry, or tracking.

Communication initiated by the extension is limited to the local Codex app-server. Codex and any services it communicates with are governed by their own terms and privacy policies.

## Data storage

The extension does not create an external user account or remote database. Settings and temporary runtime state remain on the user's Windows device. Daily token totals and per-file read positions are kept only in memory and are rebuilt from the current weekly window after a restart. To keep the weekly usage trend available after Command Palette restarts, it stores a rolling maximum of seven days of local timestamps and remaining weekly-percentage measurements. When the adaptive weekly forecast is enabled, it also stores at most eight aggregated quota-cycle profiles: total observed duration and consumption, plus six-hour usage buckets relative to the reset. These files contain no account, session, prompt, or message content and are never transmitted. Users can pause learning while keeping those profiles; measurements collected while paused are not added later. Users can also delete the learned profiles from Codex Usage settings.

## Permissions

The Windows `runFullTrust` capability is required to run the packaged Command Palette COM server and to communicate with the locally installed Codex process. It is not used to bypass Windows security controls or access unrelated user data.

## Contact

Questions or privacy requests can be submitted through the public issue tracker:

https://github.com/TheBeems/CodexUsageDock/issues
