# AGENTS.md

This file defines the working agreement for AI coding agents and human contributors in this repository. It applies to the entire repository unless a more specific `AGENTS.md` exists in a subdirectory.

## Project Overview

Codex Usage Dock is a Windows Command Palette extension that displays local Codex usage information in the PowerToys Dock. It is a .NET 10, Windows-only application packaged as MSIX for x64 and ARM64.

Key locations:

- `CodexUsageDock/`: application source, manifest, assets, and publish profiles.
- `CodexUsageDock.Tests/`: xUnit tests.
- `scripts/`: build and release automation.
- `packaging/`: distribution-related files.
- `README.md`: user-facing product and installation documentation.
- `DEVELOPMENT.md`: build, deployment, packaging, and release instructions.

## Core Engineering Principles

The following principles are mandatory for every change:

### KISS (Keep It Simple, Stupid)

- Implement the simplest solution that fully satisfies the requirement.
- Prefer clear control flow and explicit names over cleverness, premature abstraction, or unnecessary indirection.
- Do not introduce a framework, dependency, configuration option, service, or architectural layer without a demonstrated need.
- Keep methods and types focused. Split them when doing so makes the code easier to understand, test, or maintain.
- Optimize only when measurements or a concrete constraint justify it.

### DRY (Don't Repeat Yourself)

- Keep each business rule and piece of knowledge in one authoritative place.
- Reuse existing helpers, models, constants, and build scripts instead of duplicating behavior.
- Extract shared logic when duplication is meaningful and likely to evolve together.
- Do not force unrelated code through a common abstraction merely because it looks similar. A small amount of local repetition is preferable to the wrong abstraction.
- Keep documentation synchronized by linking to the canonical source instead of copying large sections between files.

KISS and DRY must be balanced: remove meaningful duplication without creating abstractions that make the code harder to follow.

## Working Method

1. Read the relevant source, tests, and documentation before making changes.
2. Keep the change narrowly scoped to the requested outcome. Avoid unrelated refactors or formatting churn.
3. Preserve existing public behavior unless the task explicitly changes it.
4. Add or update tests for fixes, business rules, parsing, formatting, and edge cases.
5. Run the smallest relevant verification first, then the broader test/build checks warranted by the change.
6. Update `CHANGELOG.md` for every repository change. Add a concise, user-focused entry in the appropriate Keep a Changelog category, link it to the implementing commit or pull request before committing or merging the change, and move Unreleased entries into a dated version section when that version is released.
7. Update other user or developer documentation when behavior, requirements, setup, packaging, or release steps change.
8. Report what changed, which checks ran, and any remaining risk or unverified behavior.

Do not modify files generated under `bin/`, `obj/`, or `artifacts/` by hand. Do not commit secrets, signing material, local machine paths, temporary diagnostics, or unpublished credentials.

## C# and .NET Conventions

- Follow the repository's existing C# style and the settings in `Directory.Build.props`.
- Keep nullable reference types enabled and address nullability warnings instead of suppressing them without justification.
- Respect the enabled .NET analyzers. New warnings should be fixed or explicitly justified.
- Prefer immutable data and narrow visibility where practical.
- Use descriptive names that express intent. Comments should explain constraints or reasoning, not restate the code.
- Pass `CancellationToken` through asynchronous operations where cancellation is meaningful.
- Dispose owned resources deterministically with `using`, `await using`, or `IDisposable` as appropriate.
- Avoid blocking asynchronous work with `.Result`, `.Wait()`, or unnecessary synchronous waits.
- Validate data received from processes, JSON, files, and other external boundaries. Fail safely and preserve useful diagnostic context.
- Do not add a NuGet dependency when the platform or existing dependencies already provide a clear, maintainable solution.

## Architecture and Platform Constraints

- Preserve the out-of-process COM server hosting pattern in `Program.cs` unless the task explicitly requires an architectural change.
- Keep the extension GUID/CLSID consistent between the C# registration and every corresponding entry in `Package.appxmanifest`.
- Keep runtime-identifier builds self-contained. Do not set `SelfContained=false`; the project contains an explicit guard because that configuration breaks the packaged .NET host.
- Maintain support for both `win-x64` and `win-arm64` unless a requirement explicitly changes supported architectures.
- Keep expensive I/O, process startup, parsing, and logging out of frequently called UI methods such as item getters.
- Keep UI updates thread-safe and use the Command Palette notification APIs when observable state changes.
- Treat local Codex app-server and session data as untrusted input: tolerate missing, malformed, stale, or version-skewed fields.
- Preserve the documented fallback behavior unless intentionally changing it with tests and documentation.

## Testing and Verification

Use the commands appropriate to the change:

```powershell
dotnet restore .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -p:Platform=x64 -r win-x64 -p:SelfContained=true
dotnet test .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -c Debug -p:Platform=x64 -r win-x64 -p:SelfContained=true --no-restore
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=x64 -r win-x64 --self-contained true
```

Also verify ARM64 when changing project files, runtime-sensitive code, native/COM integration, manifests, publishing, or packaging:

```powershell
dotnet restore .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -p:Platform=ARM64 -r win-arm64 -p:SelfContained=true
dotnet test .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -c Debug -p:Platform=ARM64 -r win-arm64 -p:SelfContained=true --no-restore
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=ARM64 -r win-arm64 --self-contained true
```

Always keep `Platform` and the RID paired (`x64` with `win-x64`, `ARM64` with `win-arm64`). On ARM64 Windows, running the x64 testhost also requires an installed x64 .NET 10 runtime; the application's self-contained RID build does not provide the separate testhost runtime. Prefer the native ARM64 testhost when x64 emulation is not part of the change being verified.

Testing expectations:

- A bug fix should include a regression test that fails before the fix and passes afterward when practical.
- Tests must be deterministic and independent of network access, local Codex authentication, wall-clock timing, locale, and execution order unless the test explicitly controls those inputs.
- Prefer testing externally observable behavior over implementation details.
- Do not weaken, skip, or delete a test merely to make a change pass. If expected behavior changes, update the test and explain why.
- A successful build alone does not prove MSIX registration or Command Palette integration. For integration changes, run `scripts/test-integration.ps1`, follow the deployment and **Reload Command Palette Extension** steps in `DEVELOPMENT.md`, and clearly state when manual verification was not performed.

## Security and Privacy

- Keep usage processing local, consistent with `PRIVACY.md`.
- Never log access tokens, authorization headers, full session payloads, personal identifiers, or other secrets.
- Use least privilege for process, file, registry, and package access.
- Do not execute shell commands assembled from untrusted input. Prefer structured APIs and explicit argument passing.
- Normalize and validate paths before file access, and avoid broad filesystem scans when a narrow known location is sufficient.
- Preserve useful error information without exposing sensitive data to the UI or logs.

## Packaging and Release Safety

- Treat package identity, publisher, Store ID, capabilities, architecture declarations, and manifest registrations as release-critical.
- Do not change versions, Store identity, publisher data, signing configuration, or release workflows unless the task explicitly requires it.
- Use `scripts/build-release.ps1` for Store package creation as documented in `DEVELOPMENT.md`.
- Never distribute unsigned artifacts from `artifacts/store` as production packages.
- Keep release changes reproducible; avoid steps that depend on undocumented local machine state.

## Documentation and Change Quality

- Write documentation and user-facing text in clear English unless a task specifies another language.
- Keep `CHANGELOG.md` current for every repository change; it is the canonical, user-facing history of shipped and unreleased work.
- Keep `README.md`, `DEVELOPMENT.md`, and `PRIVACY.md` aligned with actual behavior.
- Preserve backward compatibility when practical. Call out intentional breaking changes explicitly.
- Keep commits and pull requests focused, with a concise explanation of the problem, solution, validation, and risks.
- Leave the repository cleaner only within the scope of the task; record unrelated issues instead of expanding the change silently.

## Definition of Done

A change is complete when:

- the requested behavior is implemented with mandatory KISS and DRY compliance;
- relevant tests are added or updated and pass;
- applicable analyzer, build, and architecture checks pass;
- security, privacy, COM/MSIX, and packaging implications have been considered;
- `CHANGELOG.md` has been updated with a user-focused entry and, before the change is committed or merged, a link to the implementing commit or pull request;
- affected documentation is updated;
- no generated artifacts, secrets, or unrelated changes are included;
- any manual verification gaps or remaining risks are clearly disclosed.
