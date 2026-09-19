# AGENTS.md

These instructions apply throughout the repository. More specific directory instructions override conflicting rules for their scope; all other rules still apply.

## Project Overview

Codex Usage Dock is a Windows Command Palette extension that displays local Codex usage information in the PowerToys Dock. It is a .NET 10, Windows-only application packaged as MSIX for x64 and ARM64.

- `CodexUsageDock/`: application, manifest, assets, and publish profiles.
- `CodexUsageDock.Tests/`: xUnit tests and shared `TestEnvironment`.
- `scripts/` and `packaging/`: build, verification, and distribution tooling.
- [README.md](README.md): product and installation guidance.
- [DEVELOPMENT.md](DEVELOPMENT.md): canonical setup, build, test, integration, and release procedures.

## Scope and Working Method

- Read the relevant source, tests, documentation, and Git diff before editing. Preserve existing user changes and keep the work scoped to the requested outcome.
- Review-only requests remain read-only: report findings without editing files or changing the installed application. Report unrelated issues instead of fixing them as part of the task.
- For implementation requests, complete authorized edits and verification autonomously. Resolve routine choices using repository conventions; ask only when missing information materially affects scope, behavior, or authorization.
- Commit, push/merge, publication, and installation are separately requestable stages. Perform only authorized stages and reuse authorization already given for the same scope. Prepare a concrete, reviewable result before requesting any additional authorization.
- Do not hand-edit generated files under `bin/`, `obj/`, or `artifacts/`. Keep secrets, signing material, local machine paths, temporary diagnostics, and generated outputs out of commits.

## Engineering Rules

- **KISS is mandatory:** use the simplest solution that satisfies the requirement, with clear names, focused types, and explicit control flow. Add dependencies, configuration, services, or layers only for a demonstrated need; optimize only for a measured issue or concrete constraint.
- **DRY is mandatory:** reuse existing helpers, models, constants, and scripts. Keep business rules in one authoritative place and link to canonical documentation. Extract meaningful shared logic without forcing unrelated behavior through an abstraction.
- Preserve public behavior and documented fallbacks unless the task intentionally changes them, with tests and documentation for the change.
- Follow existing C# style and `Directory.Build.props`; retain nullable reference types and enabled analyzers. Fix new warnings or explicitly justify them instead of silently suppressing them.
- Prefer immutable data and narrow visibility where practical. Comments should explain constraints or reasoning.
- Pass `CancellationToken` where cancellation matters, dispose owned resources deterministically, and avoid blocking asynchronous work with `.Result` or `.Wait()`.

## Architecture and Platform Constraints

- Preserve the out-of-process COM server hosting pattern in `Program.cs` unless the task explicitly requires an architectural change.
- Keep the extension GUID/CLSID consistent between the C# registration and every corresponding entry in `Package.appxmanifest`.
- Keep runtime-identifier builds self-contained. Do not set `SelfContained=false`; the project guard prevents a broken packaged .NET host.
- Maintain support for both `win-x64` and `win-arm64` unless a requirement explicitly changes supported architectures.
- Keep expensive I/O, process startup, parsing, and logging out of frequently called UI methods such as item getters.
- Keep UI updates thread-safe and use the Command Palette notification APIs when observable state changes.
- Validate process, JSON, file, and other external input. In particular, tolerate missing, malformed, stale, or version-skewed Codex app-server and session fields.

## Testing and Verification

Use the [setup instructions](DEVELOPMENT.md#prerequisites) and [verification commands](DEVELOPMENT.md#verify-the-application) from the repository root. Start with the smallest relevant check, then complete the broader checks warranted by the change. Reuse successful checks for unchanged inputs; repeat or expand them only for new changes, failures, or unresolved concerns.

| Change | Required verification |
| --- | --- |
| Documentation only | Check accuracy, links, and the diff; application tests and builds are unnecessary. |
| Logic, parsing, formatting, or bug fix | Add or update meaningful behavior tests, including a regression that fails before the fix when practical. Run affected tests, then the native test suite and application build as warranted. |
| Project files, runtime-sensitive code, native/COM integration, manifests, publishing, or packaging | Build both x64 and ARM64; run relevant tests for both architectures on compatible hosts. Include integration checks for changes affecting registration or Command Palette behavior. |

- Keep `Platform` and RID paired: `x64` / `win-x64`, `ARM64` / `win-arm64`. Prefer the native testhost. An ARM64 build on x64 does not execute ARM64 tests; x64 tests on ARM64 require the separate x64 .NET 10 runtime. Self-contained application builds do not supply the testhost runtime.
- Use a compatible machine or CI for required architecture execution unavailable locally. If none is available, finish independent checks and report that verification as pending; do not count a cross-build as runtime verification.
- Unit tests must use [TestEnvironment](CodexUsageDock.Tests/TestEnvironment.cs) or explicitly inject isolated temporary storage for every settings/history/output path. Never use the production parameterless `CodexUsageService` constructor or write synthetic data to real user storage. Follow the fixture guidance in DEVELOPMENT.md.
- Keep tests deterministic: control time, locale, and other variable inputs; avoid network access, local authentication, and execution-order dependencies. Test observable behavior. Do not weaken, skip, or delete tests merely to obtain a pass; explain intentional expectation changes.

### Integration Checks

For changes affecting registration or Command Palette behavior:

1. After building, run `scripts/test-integration.ps1` with the matching `-Architecture` and without `-Register`. This checks build outputs and existing registration without changing installation.
2. Registration is a separate installation action: use `-Register` only within authorized scope. Prefer an isolated environment. When the user explicitly requests testing in their current account, use the backed-up `scripts/test-local.ps1` workflow in [DEVELOPMENT.md](DEVELOPMENT.md#test-in-your-current-windows-account); it may replace the Store registration and closes the running extension. A missing registration is not permission to install.
3. For authorized integration work, complete **Reload Command Palette Extension** and the applicable functional checks in DEVELOPMENT.md. Report preflight warnings and unavailable UI checks explicitly. A passing build or preflight does not establish successful COM activation or working UI behavior.

## Security and Privacy

- Keep usage processing local, consistent with [PRIVACY.md](PRIVACY.md).
- Never log access tokens, authorization headers, full session payloads, personal identifiers, or other secrets.
- Use least privilege for process, file, registry, and package access, following the active sandbox policy and explicit environment instructions.
- Do not execute shell commands assembled from untrusted input. Prefer structured APIs and explicit argument passing.
- Normalize and validate paths before file access, and avoid broad filesystem scans when a narrow known location is sufficient.
- Preserve useful error information without exposing sensitive data to the UI or logs.

## Packaging and Release Safety

- Treat package identity, publisher, Store ID, capabilities, architecture declarations, and manifest registrations as release-critical. Change versions, identity, publisher data, signing configuration, or release workflows only when the task explicitly requires it.
- Use `scripts/build-release.ps1` for Store package creation as documented in DEVELOPMENT.md. For release work, follow the [release skill](.github/skills/publish-extension/SKILL.md) within the authorized stages.
- Never distribute unsigned artifacts from `artifacts/store` as production packages.
- Keep release changes reproducible; avoid steps that depend on undocumented local machine state.

## Documentation and Change Quality

- Write documentation and user-facing text in clear English unless a task specifies another language.
- Add a concise entry under the appropriate `Unreleased` category in [CHANGELOG.md](CHANGELOG.md) for each repository change. An uncommitted entry may omit its link. Add the implementing commit or PR link once it exists, before merging a PR or publishing a release; for a direct commit, add the link in a follow-up documentation commit. Completing these links does not need another changelog entry. Never invent a hash or a PR URL. At release time, move entries into a dated version section.
- Update affected product, setup, development, and privacy documentation when behavior or procedures change; explicitly describe intentional breaking changes.
- Keep commit and PR descriptions focused on the problem, resulting behavior, validation, and remaining risks.

## Definition of Done

Review the final diff against the requested outcome and the applicable rules above. Report what changed, the checks performed and their results, and any remaining risk or pending verification. A fully verified change requires all applicable checks to pass; distinguish completed implementation from blocked architecture or manual checks, and state the next step. Claim committed, published, installed, or functionally verified only when that stage actually completed.
