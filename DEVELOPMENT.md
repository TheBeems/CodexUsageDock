# Development

## Prerequisites

- Windows 10 build 19041 or newer, or Windows 11
- .NET 10 SDK
- PowerToys 0.100.0 or newer
- Windows SDK with `makeappx.exe` for Store packaging
- Visual Studio 2022 with Windows application development tools for interactive packaging work

## Build and register the development package

```powershell
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=ARM64
Add-AppxPackage -Register .\CodexUsageDock\bin\ARM64\Debug\net10.0-windows10.0.26100.0\win-arm64\AppxManifest.xml
```

Use `x64` instead of `ARM64` on Intel and AMD machines.

Development RID builds are self-contained. Do not override `SelfContained=false`: MSIX tooling places app-local .NET host files in the output, and combining those files with a framework-dependent runtime configuration prevents the host from finding either an app-local or machine-wide framework.

## Verify the application

```powershell
dotnet restore .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -p:Platform=x64 -r win-x64 -p:SelfContained=true
dotnet test .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -c Debug -p:Platform=x64 -r win-x64 -p:SelfContained=true --no-restore
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=x64 -r win-x64 --self-contained true
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=ARM64 -r win-arm64 --self-contained true
```

## Build the Microsoft Store package

`scripts/build-release.ps1` is the only release package builder. It always creates one self-contained x64+ARM64 Store upload in Release configuration:

```powershell
.\scripts\build-release.ps1 -Version 0.2.1
```

The command temporarily applies MSIX version `0.2.1.0`, then restores `Package.appxmanifest` byte-for-byte. It validates:

- package name `TheBeems.CodexUsageDock`;
- publisher `CN=F748B633-A4F0-42F4-B6F1-B5BDCAED8E0C`;
- x64 and ARM64 architectures;
- app-local .NET runtime files and `includedFrameworks`;
- packaged COM and `com.microsoft.commandpalette` registrations;
- one bundle inside the final `.msixupload`.

Successful output contains only:

```text
artifacts/store/CodexUsageDock-0.2.1.msixupload
artifacts/store/SHA256SUMS.txt
```

The packages inside `.msixupload` are intentionally unsigned. Partner Center signs packages during Store publication. Never distribute an Actions artifact or a locally built Store upload as an installer.

## GitHub workflow

`.github/workflows/release.yml` is the only release workflow.

- Pull requests restore, run the x64 test suite, build Debug x64 and ARM64, and validate a Store upload with the non-release version `0.0.1`. No artifact is uploaded.
- A manual workflow run accepts one three-part release version and retains the `.msixupload` plus checksum for 30 days.
- The workflow has read-only repository permissions and uses no deployment environment, signing identity, Partner Center credentials, or publication API.

## Publish version 0.2.1

1. Merge the tested release-flow change into `main`.
2. Run **Store package** manually with version `0.2.1`.
3. Download and extract the `CodexUsageDock-0.2.1-store` Actions artifact.
4. In Partner Center, open existing product `9NFCPJXQG9FG` and upload `CodexUsageDock-0.2.1.msixupload` to a new submission.
5. Complete the required Store listing and testing notes, then submit for certification.
6. Stop on rejection and address the reported issue in a new package version. Do not use a self-signed or alternate distribution fallback.
7. After the Store listing is publicly installable, verify x64 and ARM64 installation, removal, Command Palette discovery, and live Dock usage.
8. Update `README.md` with `https://apps.microsoft.com/detail/9NFCPJXQG9FG`.
9. Tag the exact published commit as `v0.2.1` and create a GitHub Release containing release notes and the Store link only. Do not attach package files.

Release `v0.2.0` remains unchanged.

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
