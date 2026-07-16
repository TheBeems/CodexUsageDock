# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Each entry links to the commit or pull request that introduced the change.

## [Unreleased]

### Fixed

- Microsoft Store packages now include the complete self-contained .NET application layout, preventing startup failures caused by a missing managed entry assembly. ([PR #11](https://github.com/TheBeems/CodexUsageDock/pull/11))

## [0.5.1] - 2026-07-16

### Changed

- Reset credits in the Dock now use a plain `resets` label and show the whole hours or days until the next credit expires. ([PR #10](https://github.com/TheBeems/CodexUsageDock/pull/10))

## [0.5.0] - 2026-07-15

### Changed

- The usage page now compares allowance used with elapsed window time, adds a reset projection for each active quota window, and keeps reset credits, account status, and data-source details in the native Command Palette Details pane. ([PR #9](https://github.com/TheBeems/CodexUsageDock/pull/9))

## [0.4.0] - 2026-07-14

### Added

- The usage-details Ctrl+K menu now opens Codex Usage settings next to the manual refresh action. ([PR #8](https://github.com/TheBeems/CodexUsageDock/pull/8))
- Separate five-hour and weekly usage trends, including a local rolling seven-day weekly history that survives Command Palette restarts. ([PR #7](https://github.com/TheBeems/CodexUsageDock/pull/7))

### Fixed

- Five-hour trend history is no longer shown while the five-hour limit is inactive. ([PR #7](https://github.com/TheBeems/CodexUsageDock/pull/7))
- Weekly forecasts now exclude history from before the current weekly quota window, even when the app was closed across a reset. ([PR #7](https://github.com/TheBeems/CodexUsageDock/pull/7))
- Weekly limit forecasts now show a date when the projected limit is not today. ([PR #7](https://github.com/TheBeems/CodexUsageDock/pull/7))
- Development and CI now use the published .NET SDK 10.0.301, avoiding restores for unavailable 10.0.10 runtime packs. ([PR #7](https://github.com/TheBeems/CodexUsageDock/pull/7))
- Development documentation now makes the repo-local SDK bootstrap work even without a system .NET 10 host. ([PR #7](https://github.com/TheBeems/CodexUsageDock/pull/7))

## [0.3.0] - 2026-07-14

### Added

- A pre-Store integration smoke check now validates package registration, COM and AppExtension metadata, and Command Palette readiness on x64 and ARM64. [PR #6](https://github.com/TheBeems/CodexUsageDock/pull/6)
- Per-extension Dock display settings for the five-hour limit, weekly limit, resets and credits, reset times, and refresh interval. [commit 9ea548d](https://github.com/TheBeems/CodexUsageDock/commit/9ea548d)
- Repository contribution rules now require every change to update this changelog and link its entry to the implementing commit or pull request before it is committed or merged. [commit 192f313](https://github.com/TheBeems/CodexUsageDock/commit/192f313)

### Changed

- Usage refreshes now coalesce overlapping requests, show progress, cancel safely during shutdown, and report privacy-safe errors when live and fallback sources are unavailable. [PR #6](https://github.com/TheBeems/CodexUsageDock/pull/6)
- Store packaging now uses a consistent Codex Usage visual identity, validates artwork independently of host-specific PNG encoding and anti-aliasing, checks the complete required asset set, targets current Windows behavior, requests only the required full-trust capability, and uses isolated NuGet and build state. [PR #6](https://github.com/TheBeems/CodexUsageDock/pull/6)
- The details page now distinguishes the standalone Codex CLI app-server from local session metadata, which may have been written by another local Codex client. [commit 192f313](https://github.com/TheBeems/CodexUsageDock/commit/192f313)
- Future production releases use the Microsoft Store as the sole distribution and update channel. [commit 0440e98](https://github.com/TheBeems/CodexUsageDock/commit/0440e98cb1aaa01d75595e52fdb0fcd333378006)
- The Store release workflow now takes its version from the project file, avoiding a separately maintained release version. [commit 4a10eb2](https://github.com/TheBeems/CodexUsageDock/commit/4a10eb2a94f16d55b59fdefe790c79b3d4f98e17)

### Fixed

- App-server requests are trimming-safe, malformed reset-credit fields and external display text no longer break or distort live data, locale-sensitive reset formatting is explicit, and the details-page title is spelled correctly. [PR #6](https://github.com/TheBeems/CodexUsageDock/pull/6)

### Removed

- Downloadable GitHub installer releases have been retired in favour of Microsoft Store delivery. [commit 0440e98](https://github.com/TheBeems/CodexUsageDock/commit/0440e98cb1aaa01d75595e52fdb0fcd333378006)

## [0.2.0] - 2026-07-13

### Added

- A more detailed usage dashboard with historical usage trends. [PR #2](https://github.com/TheBeems/CodexUsageDock/pull/2)
- Credit balance and reset-credit information in the usage details. [PR #1](https://github.com/TheBeems/CodexUsageDock/pull/1)
- A fallback that reads usage from the standalone Codex CLI, including its npm wrapper, when the app-server data is unavailable. [PR #4](https://github.com/TheBeems/CodexUsageDock/pull/4)
- Downloadable GitHub release installers. [commit f2df178](https://github.com/TheBeems/CodexUsageDock/commit/f2df17820740ef53f6af34760fc7c4a83bf19472)

### Changed

- The extension now presents its usage interface in English and handles unavailable rate-limit data more gracefully. [commit d018a5b](https://github.com/TheBeems/CodexUsageDock/commit/d018a5b451be69af56ff56ab4aa42cafb5acf9af)
- Packaged x64 and ARM64 builds are self-contained, and the usage page shows the installed app version. [commit a95a428](https://github.com/TheBeems/CodexUsageDock/commit/a95a428be594103f4048a54498dca0f5602c279d)
- Distribution moved to a Microsoft Store MSIX package and gained a privacy policy. [commit 3d5ed68](https://github.com/TheBeems/CodexUsageDock/commit/3d5ed68ce8df4fc7025a1e352700845830c69a91), [commit 44bbf74](https://github.com/TheBeems/CodexUsageDock/commit/44bbf743de45ce0134fc828874c28c6cd7fd56ea)

### Fixed

- Usage history remains chronological, trend estimates are more reliable, and stale history is not shown when primary usage data is unavailable. [PR #2](https://github.com/TheBeems/CodexUsageDock/pull/2)
- Reading Codex usage data now falls back safely when rate-limit windows are unknown. [PR #3](https://github.com/TheBeems/CodexUsageDock/pull/3)

## [0.1.0] - 2026-07-12

### Added

- Initial release of the Windows Command Palette extension for viewing local Codex usage. [commit ac72fe5](https://github.com/TheBeems/CodexUsageDock/commit/ac72fe50fcd1af36f41cda896f1d792899573351)
- Automated release installer creation and smoke-test handling. [commit 64a3305](https://github.com/TheBeems/CodexUsageDock/commit/64a33058b7486dea12f026561c545b362eb2d622), [commit 21790a1](https://github.com/TheBeems/CodexUsageDock/commit/21790a1ea60a9bfec14ec578d77356c5576472fb)

[Unreleased]: https://github.com/TheBeems/CodexUsageDock/compare/v0.5.0...main
[0.5.0]: https://github.com/TheBeems/CodexUsageDock/releases/tag/v0.5.0
[0.4.0]: https://github.com/TheBeems/CodexUsageDock/releases/tag/v0.4.0
[0.3.0]: https://github.com/TheBeems/CodexUsageDock/releases/tag/v0.3.0
[0.2.0]: https://github.com/TheBeems/CodexUsageDock/releases/tag/v0.2.0
[0.1.0]: https://github.com/TheBeems/CodexUsageDock/releases/tag/v0.1.0
