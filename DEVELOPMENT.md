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

- `CodexUsageDockProvider.cs` exposes the Command Palette provider.
- `CodexUsageDockCommandsProvider.cs` provides commands and the Dock band.
- `CodexUsageService.cs` reads limits from the local Codex app-server and falls back to local session metadata.
- `Package.appxmanifest` contains the packaged COM server and Command Palette AppExtension registration.
