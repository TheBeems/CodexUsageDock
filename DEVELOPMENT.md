# Development

## Prerequisites

- Windows 10 build 19041 or newer, or Windows 11
- .NET 10 SDK
- PowerToys 0.100.0 or newer
- Visual Studio 2022 with Windows application development tools for interactive packaging work

## Build and register the development package

```powershell
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=ARM64
Add-AppxPackage -Register .\CodexUsageDock\bin\ARM64\Debug\net10.0-windows10.0.26100.0\win-arm64\AppxManifest.xml
```

Use `x64` instead of `ARM64` on Intel and AMD machines.

Development RID builds are self-contained. Do not override `SelfContained=false`: MSIX tooling places
app-local .NET host files in the output, and combining those files with a framework-dependent
runtime configuration prevents the host from finding either an app-local or machine-wide framework.

## Build GitHub release installers

Install Inno Setup 6, then run:

```powershell
winget install JRSoftware.InnoSetup
.\scripts\build-github-release.ps1 -Version 0.2.0
```

The script creates self-contained, per-user x64 and ARM64 installers plus `SHA256SUMS.txt` in `artifacts\installers`. The installers use the unpackaged Command Palette distribution mode and register the COM server under the current user.

Push a semantic version tag to publish a GitHub release:

```powershell
git tag -a v0.2.0 -m "Release v0.2.0"
git push origin v0.2.0
```

The **GitHub Release** workflow tests the application, builds both installers, smoke-tests x64 installation and uninstallation, and publishes the installers with checksums. GitHub installers are currently unsigned and can trigger a Windows SmartScreen warning.

## Build Microsoft Store packages

```powershell
$version = '0.2.0'
.\scripts\build-release.ps1 -Version $version
```

The script builds self-contained unsigned x64 and ARM64 MSIX packages in `artifacts\store`. Their identity matches Microsoft Store product `9NFCPJXQG9FG`. The Microsoft Store signs the packages after certification; do not distribute the unsigned artifacts directly.

The **Build Store package** GitHub Actions workflow provides the same build as a manually triggered artifact. It intentionally does not publish unsigned packages to GitHub Releases.

## Store identity

- Package name: `TheBeems.CodexUsageDock`
- Publisher: `CN=F748B633-A4F0-42F4-B6F1-B5BDCAED8E0C`
- Publisher display name: `TheBeems`
- Store ID: `9NFCPJXQG9FG`

## Architecture

- `CodexUsageDock.cs` exposes the Command Palette extension.
- `CodexUsageDockCommandsProvider.cs` provides commands and the Dock band.
- `CodexUsageService.cs` reads limits from the local Codex app-server and falls back to local session metadata.
- `Package.appxmanifest` contains the packaged COM server and Command Palette AppExtension registration.
