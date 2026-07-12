# Development

## Prerequisites

- Windows 10 build 19041 or newer, or Windows 11
- .NET 10 SDK
- PowerToys 0.100.0 or newer with Command Palette
- Inno Setup 6 for building release installers

Install Inno Setup with:

```powershell
winget install JRSoftware.InnoSetup
```

## Build the packaged development version

Choose the platform that matches the development machine:

```powershell
dotnet restore CodexUsageDock.sln -p:Platform=ARM64
dotnet build CodexUsageDock.sln -c Debug -p:Platform=ARM64 --no-restore
```

Use `x64` instead of `ARM64` on an Intel or AMD Windows PC.

## Build release installers

From the repository root:

```powershell
.\scripts\build-release.ps1 -Version 0.1.0
```

Build only one architecture when iterating:

```powershell
.\scripts\build-release.ps1 -Version 0.1.0 -Platforms arm64
```

Outputs are written to `artifacts/installers`:

- `CodexUsageDock-<version>-x64-setup.exe`
- `CodexUsageDock-<version>-arm64-setup.exe`
- `SHA256SUMS.txt`

The installer build uses `DistributionMode=Installer`. This produces a self-contained unpackaged executable and registers its COM LocalServer under the current user, following Microsoft's Command Palette WinGet distribution model.

## Test an installer

Install silently:

```powershell
.\artifacts\installers\CodexUsageDock-0.1.0-arm64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Confirm that this registry key exists:

```text
HKCU\Software\Classes\CLSID\{35b848eb-6ef7-4b5d-9f8b-49b5614abb48}\LocalServer32
```

Then reopen Command Palette and confirm that **Codex Usage** appears under **Settings > Extensions** and can be added to the Dock.

Uninstall through Windows Settings or run:

```powershell
& "$env:LOCALAPPDATA\Programs\CodexUsageDock\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

## Create a release

The release workflow runs for semantic version tags beginning with `v`:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

GitHub Actions then:

1. builds self-contained x64 and ARM64 installers;
2. smoke-tests installation and uninstallation of the x64 installer on a clean Windows runner;
3. uploads both build artifacts;
4. generates `SHA256SUMS.txt`;
5. creates a GitHub Release with generated release notes.

The workflow can also be run manually to validate a version without publishing a GitHub Release.

## WinGet publishing

The initial WinGet manifests are kept in `packaging/winget`. Validate them with:

```powershell
winget validate --manifest .\packaging\winget
```

The first package version is submitted to `microsoft/winget-pkgs` manually. After that PR has been accepted, `.github/workflows/update-winget.yml` submits updates whenever a new GitHub Release is published.

The update workflow requires a repository secret named `WINGET_PAT`. Use a dedicated classic GitHub token with `public_repo` access for WinGetCreate submissions. Do not reuse a broader personal token. See the [WingetCreate token guidance](https://github.com/microsoft/winget-create/blob/main/doc/token.md).

Every default-locale manifest must retain this tag so Command Palette can discover the package:

```yaml
Tags:
- windows-commandpalette-extension
```

## Architecture

- `CodexUsageService.cs` handles app-server communication and the local-session fallback.
- `UsageDockItem.cs` supplies Dock labels and details.
- `CodexUsageDockCommandsProvider.cs` exposes the Command Palette provider and Dock band.
- `installer/CodexUsageDock.iss` installs the files and registers the COM LocalServer.
- `scripts/build-release.ps1` creates reproducible release assets.
- `packaging/winget` contains the source manifests for the first WinGet submission.
- `.github/workflows/update-winget.yml` automates later WinGet update PRs.
