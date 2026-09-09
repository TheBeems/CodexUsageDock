# Usage assistant implementation

This series implements the recommended Codex-first roadmap, followed by a small, optional Claude pilot. Source work uses separate feature branches and pull requests. Merge, Store publication, and installation are separate steps.

| Sprint | Feature branch | Scope | Status |
| --- | --- | --- | --- |
| 1 | `codex/sprint-1-reliable-usage` | Modern quota categories, consistent freshness, last confirmed data, account-scoped history, safe diagnostics, version communication | [PR #18](https://github.com/TheBeems/CodexUsageDock/pull/18); 186 native ARM64 tests passed; x64/ARM64 Debug builds passed |
| 2 | `codex/sprint-2-attention-controls` | Quiet alerts, compact and individually pinnable Dock entries, account activity where supported, explicit source configuration | [PR #19](https://github.com/TheBeems/CodexUsageDock/pull/19); 235 x64 tests, both architecture builds, and package validation passed in CI |
| 3 | `codex/sprint-3-history-planning` | Retained aggregates and export, workday planning, forecast explanation and validation, supported task analysis, explicit earned-reset action | [PR #20](https://github.com/TheBeems/CodexUsageDock/pull/20); 314 native ARM64 tests; x64 tests, both builds, and package validation passed in CI |
| 4 | `codex/sprint-4-provider-pilot` | Optional Claude statusline bridge, explicit local profiles/WSL paths, efficient fallback reads, accessible text alternatives | 360 native ARM64 tests and both architecture builds passed; PR preparation complete |

Later sprints build on the previous feature branch so each PR can show only its own increment. Merge in sprint order and retarget dependent PRs to `main` after their base is merged. No merge is performed as part of this implementation request.

## Acceptance and verification

- Preserve old CLI compatibility and optional-field availability. Unknown values must never turn into zero.
- Keep account, category, provider, source, and timestamp semantics explicit; do not mix quota percentages or treat local tokens as billed cost.
- Only fresh, attributable measurements can trigger account alerts or train persistent forecasts.
- Keep local storage bounded and export explicit. Do not modify authentication or a user's existing Claude statusline automatically.
- Test parsing, state transitions, persistence, cancellation, and unsafe inputs using isolated synthetic fixtures.
- Run native ARM64 tests and x64/ARM64 builds; use PR CI for its required x64 tests and Store-package checks. Record integration limitations separately from build results.

Gemini/Cursor/Copilot expansion, a standalone tray app, cloud sync, team dashboards, and a full session manager remain deferred as recommended by the research. Forecast quality labels describe available evidence and are not a claim of empirically calibrated confidence.

### Sprint 1 verification

Native ARM64 tests passed (186/186); application builds have no warnings. Existing test-name analyzer warnings remain unchanged. The x64 .NET 10 test runtime is unavailable locally, so x64 test execution belongs to PR CI. The integration preflight passed manifest, COM identity, generated-output freshness, self-contained runtime, and asset checks. Package registration and AppExtension discovery were unavailable in this test context; Command Palette reload, visual behavior, Store installation, and real-account compatibility were not verified. Tests use isolated synthetic data and do not consume actual reset credits.

The final sprint 1 head also passed [GitHub Actions](https://github.com/TheBeems/CodexUsageDock/actions/runs/34379840746), including x64 tests, both architecture builds, and Store-package validation.

### Sprint 2 verification

The x64 and ARM64 application code compiles without warnings. Local ARM64 test execution was blocked while loading the assembly by Windows Application Control (`0x800711C7`), including a retry with elevated execution. No Windows security policy was changed. [GitHub validation](https://github.com/TheBeems/CodexUsageDock/actions/runs/34383095096) passed all 235 tests, both architecture builds, and package checks on head `d067295`. Live account activity, notifications, individual pinning, and configuration changes still require Command Palette verification after an authorized installation.

### Sprint 3 verification

Native ARM64 test execution was available again and passed 314 tests. Both application architecture builds passed without warnings. Tests cover retention/export scope, planner assumptions and held-out observations, optional protocol support, task-request ordering, and persistent reset idempotency across ambiguous results and restarts. No real reset was redeemed. Command Palette forms, confirmations, and real-account compatibility still need live verification after an authorized installation. The final head `ebe7efe` passed all 314 x64 tests, both builds, and package validation in [GitHub Actions](https://github.com/TheBeems/CodexUsageDock/actions/runs/34385299160).

Both integration preflights passed source/generated manifest, identity, asset, and self-contained runtime checks. The registered ARM64 Store package was healthy and discoverable, but its process and registration do not point at the new Debug builds. These expected mismatches leave live verification of the new code open; no registration or installation was changed.

### Sprint 4 scope

The pilot uses only an explicit local Claude capture and keeps its two quota windows and refresh state independent of Codex. The optional script preserves a formatter's input or displays a standalone quota line; setup is manual and described in README. Profiles save at most eight names and validated source paths, including Windows-accessible WSL directories, without copying credentials or launching WSL. Text views expose measured values without chart or color dependence. The quota fallback has bounded content reads and caches read positions; filesystem metadata enumeration remains proportional to the session inventory.

Execution used separate protocol/integration and implementation agents, with Luna at max effort for the bounded implementation tasks. Review covered provider concurrency and profile changes. Tests use synthetic local data; no real Claude configuration, account authentication, or reset redemption is part of verification.

All 360 native ARM64 tests passed, including script execution on synthetic stdin, profile persistence and invalid input, independent provider refresh, and bounded incremental session reads. Both application builds passed with zero warnings. Only the pre-existing test-name analyzer warnings remain. The two integration preflights passed source/generated manifest, COM identity, asset, output-freshness, and self-contained runtime checks. Registration and process matching failed as expected because Command Palette runs the installed ARM64 Store package rather than either new Debug build; the x64 preflight also reports the installed architecture mismatch. No package was registered or installed. Live forms, accessibility, Dock pinning, and real-provider compatibility remain unverified; GitHub validation is recorded in the sprint PR.
