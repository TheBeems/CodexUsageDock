Status: done
Created: 2026-07-20
Updated: 2026-07-21

# Reopen Dock allowance refresh bug

## Scope

Make a completed usage refresh invalidate the Dock-band collection at the command-provider boundary, then add a regression test for the provider-level notification and the refreshed weekly allowance.

## Non-goals

- Do not change rate-limit parsing or allowance arithmetic.
- Do not touch the unrelated local-token-usage work already present in the worktree.

## Acceptance criteria

- Satisfy `.10x/specs/dock-allowance-refresh.md`.
- Preserve Dock visibility settings and IDs.
- Add deterministic regression coverage.
- Validate with the native ARM64 test suite using an isolated output path if the running extension locks Debug output.

## References

- `.10x/specs/dock-allowance-refresh.md`
- `CodexUsageDock/CodexUsageDockCommandsProvider.cs`
- `CodexUsageDock.Tests/UsageDataTests.cs`

## Assumptions

- Record-backed: the screenshot shows the details page at 34% while the Dock shows 37% after the same live refresh.
- Record-backed: `CodexUsageDockPage` raises an items-changed notification after every update; the provider currently does not.

## Journal

- 2026-07-20: Reopened from user screenshot. The running ARM64 binary contains `OnUsageUpdated`, so the earlier child-list invalidation was deployed but did not refresh the host's Dock band.
- 2026-07-20: Replaced child-list-only invalidation with provider-level Dock-band rebuilding and invalidation. The regression now asserts the provider notification and reads the replacement band.
- 2026-07-20: Focused native ARM64 test `CompletedRefreshRebuildsAndInvalidatesDockBands` passed (1/1). Full native ARM64 suite passed (113/113) using `artifacts/validation/dock-provider-refresh`.
- 2026-07-20: Stopped the active extension, built ARM64 Debug with zero warnings/errors, and ran `scripts/test-integration.ps1 -Architecture ARM64 -Configuration Debug -Register`. The manifest, registration, AppExtension discovery, and selected COM provider all passed.

## Evidence

- Provider-level `ItemsChanged` is raised once after a completed refresh and the rebuilt band exposes `5h 75%` in deterministic regression coverage.
- The full ARM64 test suite passed 113/113. The standard ARM64 Debug build passed with zero warnings/errors.
- Integration preflight passed after development registration. It proves registration/discovery and the provider process, not visual rendering.

## Review

- Verdict: pass
- Findings: The prior code only changed the internal `WrappedDockItem` list. The replacement rebuilds the same stable band ID and signals the provider boundary that the Dock host owns. It preserves settings filtering and does not alter snapshot parsing or allowance calculation.
- Residual risk: Command Palette must be manually reloaded once before visual verification; automated checks cannot prove host rendering.

## Retrospective

- A child `IListPage.ItemsChanged` event is insufficient evidence for a Dock update. Regression coverage must assert the `CommandProvider.ItemsChanged` boundary because that is where the host refreshes its band collection.

## Blockers

- None. Manual Command Palette reload remains a post-release visual verification step, not a source-code blocker.
