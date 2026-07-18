# Development

## Prerequisites

- Windows 10 build 19041 or newer, or Windows 11
- .NET SDK 10.0.301 (the repository pins this version in `global.json`)
- PowerToys 0.100.0 or newer
- Windows SDK with `makeappx.exe` for Store packaging
- Visual Studio 2022 with Windows application development tools for interactive packaging work

If SDK 10.0.301 is not installed system-wide, run the following once from the repository root. It installs the pinned SDK for the current machine architecture only in the ignored `.dotnet/` folder used by this repository:

```powershell
.\scripts\install-dotnet-sdk.ps1
```

The bootstrap script does not need an existing .NET host. Before running the build or test commands below in the same PowerShell session, put its repo-local host first on `PATH`; this also prevents an older system-wide host from ignoring `global.json`'s SDK path:

```powershell
$env:PATH = "$PWD\.dotnet;$env:PATH"
dotnet --version # 10.0.301
```

## Build and register the development package

```powershell
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=ARM64 -r win-arm64 --self-contained true
.\scripts\test-integration.ps1 -Architecture ARM64 -Register
```

Use `-p:Platform=x64 -r win-x64` and `-Architecture x64` on Intel and AMD machines. The registration script performs the manifest, package-safety, architecture, COM, and AppExtension preflight before it changes the current-user development registration.

After every build and registration:

1. Open Command Palette.
2. Run **Reload Command Palette Extension**.
3. Open **Settings > Extensions** and confirm that **Codex Usage** is enabled.

Do not register a development manifest over a Microsoft Store installation. Use an isolated Windows user or test VM for development package registration.

Development RID builds are self-contained. Do not override `SelfContained=false`: MSIX tooling places app-local .NET host files in the output, and combining those files with a framework-dependent runtime configuration prevents the host from finding either an app-local or machine-wide framework.

## Verify the application

```powershell
dotnet restore .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -p:Platform=x64 -r win-x64 -p:SelfContained=true
dotnet test .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -c Debug -p:Platform=x64 -r win-x64 -p:SelfContained=true --no-restore
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=x64 -r win-x64 --self-contained true

dotnet restore .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -p:Platform=ARM64 -r win-arm64 -p:SelfContained=true
dotnet test .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -c Debug -p:Platform=ARM64 -r win-arm64 -p:SelfContained=true --no-restore
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=ARM64 -r win-arm64 --self-contained true
```

Run a testhost only on a compatible Windows architecture. ARM64 Windows can run the x64 testhost through emulation, but the machine must also have the x64 .NET 10 runtime installed. The self-contained application build does not make the separate `dotnet test` host self-contained. On ARM64, use `& "$env:ProgramFiles\dotnet\x64\dotnet.exe" --list-runtimes` when that x64 host is installed and confirm that `Microsoft.NETCore.App 10` is listed. Native ARM64 tests use the normal ARM64 `dotnet` host.

Always specify both `Platform` and its matching RID. Omitting `-r win-x64` or `-r win-arm64` can make MSBuild combine the host architecture with a conflicting `PlatformTarget`.

## Integration smoke test

After building the matching Debug package, run the non-destructive preflight:

```powershell
.\scripts\test-integration.ps1 -Architecture x64
.\scripts\test-integration.ps1 -Architecture ARM64
```

By default, the script only reads the source and generated manifests, verifies that build outputs are current, and checks current-user package registration, package health, running process state, and the Command Palette AppExtension catalog where Windows supports the synchronous API. It reports a failure when the package is not registered. In an isolated test user or VM, registration can be requested explicitly:

```powershell
.\scripts\test-integration.ps1 -Architecture x64 -Register
.\scripts\test-integration.ps1 -Architecture ARM64 -Register
```

`-Register` never removes or replaces a non-development Store installation. It refreshes or switches an existing development package under this repository's `CodexUsageDock/bin` tree in place with `ForceUpdateFromAnyVersion`, so a failed update leaves the prior registration intact. A development registration from any other location is refused. Windows may close a running Codex Usage Dock process during this refresh. On Windows builds older than 26100, the script cannot use the synchronous `AppExtensionCatalog.FindAll()` API and reports that discovery still needs manual verification. It never automates Command Palette UI input. After it completes, run **Reload Command Palette Extension** manually.

### Pre-Store x64 and ARM64 matrix

Complete every row on a clean x64 environment and a separate clean ARM64 environment. Record the Windows and PowerToys versions, package version, date, result, and supporting screenshot or diagnostic output. Do not test both architectures by repeatedly replacing packages in a production user profile.

| Scenario | x64 | ARM64 | Acceptance criteria |
| --- | --- | --- | --- |
| Clean install | Required | Required | The signed Store test package installs without a prior package or manual dependency installation. |
| Update | Required | Required | The previous supported Store version updates in place; Command Palette discovers the new version and settings remain intact. |
| Uninstall and reinstall | Required | Required | Uninstall removes the app and extension registration; reinstall restores discovery without stale or duplicate providers. |
| Automated preflight | Required | Required | `test-integration.ps1` passes manifest, package, CLSID, COM, and AppExtension checks. |
| Start-menu visibility | Required | Required | Codex Usage does not appear as a standalone app in Start; it is activated only by Command Palette through its packaged COM registration. |
| Discovery and reload | Required | Required | After **Reload Command Palette Extension**, **Codex Usage** appears once under **Settings > Extensions** and can be enabled. |
| Details page | Required | Required | Opening **Codex Usage** shows five-hour and weekly quota summaries that compare allowance used with elapsed window time and include a projection for each active window. The weekly view spans the quota window: continuous observed allowance is solid, a fresh projection is dashed, bars show only observed consumption per local calendar day, dated weekday labels include the partial first and last reset-boundary days, and reset/current-time markers clarify the timeline. When adaptive forecasting has history, its dashed line may change at six-hour boundaries and the status line identifies the local-history depth; disabled or immature history falls back to current pace. Resets, credits, account status, and source appear in the native Details pane. Verify pace labels, inactive/projection fallback states, chart gap handling, visible 0–100% and calendar-date axes, partial-day placement, semantic status colors, narrow and wide window sizes, light/dark themes, and high contrast; manual refresh must update both areas without clipping or freezing Command Palette. |
| Dock band | Required | Required | The band can be added, each enabled item opens details, and values update while Command Palette remains responsive. |
| Settings | Required | Required | Visibility, reset-time, refresh-interval, and adaptive-forecast choices apply immediately and persist after restarting Command Palette. Verify that disabling pauses learning without replaying measurements collected while paused, retains history, and that deleting learned history requires confirmation. |
| Live app-server | Required | Required | With a signed-in standalone Codex CLI, the details page identifies the CLI app-server as the source and refreshes live data. |
| Local fallback | Required | Required | In an isolated test account with local session metadata but no launchable standalone CLI, fallback data appears and is identified as local session data. Do not rename or delete a real CLI installation to create this state. |
| No-data failure | Required | Required | In an isolated account with neither a CLI nor session data, the extension shows a bounded unavailable/error state and does not crash or loop. |

Store install, update, and uninstall behavior must be tested with a Store-signed test acquisition when it is available. Development manifest registration is sufficient only for the earlier COM activation, discovery, page, Dock, and settings checks.

## Build the Microsoft Store package

The package artwork is generated from one canonical visual mark. Treat `scripts/generate-assets.ps1` as its source instead of editing individual PNG files. The release builder compares decoded artwork with a small rendering tolerance, because PNG encoding and anti-aliasing can differ between supported build hosts without changing the design:

```powershell
.\scripts\generate-assets.ps1
```

The script overwrites all MSIX logo, tile, splash, lock-screen, and Store images at their required dimensions.

`scripts/build-release.ps1` is the only release package builder. It always creates one trimmed, self-contained x64+ARM64 Store upload in Release configuration:

First set the three-part `<Version>` in `CodexUsageDock/CodexUsageDock.csproj` to the release version, then run:

```powershell
.\scripts\build-release.ps1
```

The command builds from an isolated manifest copy with the four-part MSIX version derived from the project version (for example, `0.4.0.0`); it never rewrites the tracked `Package.appxmanifest`. It validates:

- package name `TheBeems.CodexUsageDock`;
- publisher `CN=F748B633-A4F0-42F4-B6F1-B5BDCAED8E0C`;
- x64 and ARM64 architectures;
- the complete generated asset set and its required pixel dimensions;
- the complete app-local managed application and .NET runtime layout, including self-contained `includedFrameworks` metadata;
- packaged COM and `com.microsoft.commandpalette` registrations;
- a hidden Start-menu entry point, because the executable is activated as a Command Palette COM server rather than as a standalone app;
- one bundle inside the final `.msixupload`.

Successful output contains only:

```text
artifacts/store/CodexUsageDock-0.4.0.msixupload
artifacts/store/SHA256SUMS.txt
```

The packages inside `.msixupload` are intentionally unsigned. Partner Center signs packages during Store publication. Never distribute an Actions artifact or a locally built Store upload as an installer.

## GitHub workflow

`.github/workflows/release.yml` is the only release workflow.

- Pull requests restore, run the x64 test suite, build Debug x64 and ARM64, and validate a Store upload using the project version. No artifact is uploaded.
- A manual workflow run uses the project version and retains the `.msixupload` plus checksum for 30 days.
- The workflow has read-only repository permissions and uses no deployment environment, signing identity, Partner Center credentials, or publication API.

## Publish a version

1. Merge the tested release-flow change into `main`.
2. Set the project `<Version>` to the intended release version, commit it, and run **Store package** manually.
3. Download and extract the corresponding `CodexUsageDock-<version>-store` Actions artifact.
4. In Partner Center, open existing product `9NFCPJXQG9FG` and upload the matching `.msixupload` to a new submission.
5. Complete the required Store listing and testing notes. State that this package is a PowerToys Command Palette extension, does not expose a standalone Start-menu app, and must be tested by enabling **Codex Usage** under **Command Palette > Settings > Extensions**, running **Reload Command Palette Extension**, and opening **Codex Usage** inside Command Palette. Then submit for certification.
6. Stop on rejection and address the reported issue in a new package version. Do not use a self-signed or alternate distribution fallback.
7. After the Store listing is publicly installable, verify x64 and ARM64 installation, removal, Command Palette discovery, and live Dock usage.
8. Update `README.md` with `https://apps.microsoft.com/detail/9NFCPJXQG9FG`.
9. Tag the exact published commit as `v<version>` and create a GitHub Release containing release notes and the Store link only. Do not attach package files.

Older GitHub releases remain unchanged.

## Command Palette Gallery

Open a Gallery pull request only after the Store listing is publicly installable. Follow the current [Gallery contribution guide](https://github.com/microsoft/CmdPal-Extensions/blob/main/docs/CONTRIBUTING.md) and add:

```text
extensions/thebeems/codex-usage-dock/
├── extension.json
└── icon.png
```

Use these fixed values in `extension.json`:

- ID: `thebeems.codex-usage-dock`
- install source: `{ "type": "msstore", "id": "9NFCPJXQG9FG" }`
- categories: `developer-tools`, `utilities-and-tools`
- tags: `codex`, `usage`, `quotas`, `dock`
- homepage: `https://github.com/TheBeems/CodexUsageDock`

Copy `CodexUsageDock/Assets/Square150x150Logo.scale-200.png` to `icon.png`. It is 300×300 pixels and below the Gallery size limit. Screenshots are optional and are intentionally omitted from the first submission.

## Store identity

These values are assigned to the existing Partner Center product and are release-critical:

- package name: `TheBeems.CodexUsageDock`
- publisher: `CN=F748B633-A4F0-42F4-B6F1-B5BDCAED8E0C`
- publisher display name: `TheBeems`
- Store product ID: `9NFCPJXQG9FG`

Do not change or override them in release automation.

## Architecture

- `CodexUsageDock.cs` exposes the Command Palette extension.
- `CodexUsageDockCommandsProvider.cs` provides commands and the Dock band.
- `CodexUsageService.cs` reads limits from the local Codex app-server and falls back to local session metadata.
- `Package.appxmanifest` contains the packaged COM server and Command Palette AppExtension registration.
