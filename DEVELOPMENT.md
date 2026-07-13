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

## WinGet publishing status

Do not submit the current unpackaged Inno installers to WinGet. End-to-end testing with PowerToys 0.100.2 and Command Palette 0.11 showed that they register and activate the COM server correctly, but Command Palette does not discover them as extensions.

The current loader enumerates `com.microsoft.commandpalette` entries through the Windows `AppExtensionCatalog`, which requires package-manifest registration. This conflicts with the Microsoft Learn page that documents registry-only Inno installers for WinGet. WinGet pull requests `microsoft/winget-pkgs#401316` and `#401645` were closed to avoid distributing an installer that cannot load in the current Command Palette release.

Revisit WinGet publication only when one of these conditions is met:

- Microsoft documents and ships working support for unpackaged registry-only extensions; or
- the release artifact is a packaged MSIX signed by a certificate trusted on end-user systems.

After a working package is published to WinGet, gallery visibility requires a separate pull request to `microsoft/CmdPal-Extensions`; the current in-product gallery uses that curated feed rather than automatically listing all packages carrying the WinGet tag.

## Build Microsoft Store packages

```powershell
$version = '0.2.0'
.\scripts\build-release.ps1 -Version $version
```

The script builds self-contained unsigned x64 and ARM64 MSIX packages, combines them in an MSIX bundle, and creates a Store-ready `.msixupload` file in `artifacts\store`. Their identity matches Microsoft Store product `9NFCPJXQG9FG`. The Microsoft Store signs the packages after certification; do not distribute the unsigned artifacts directly.

The **Build Store package** GitHub Actions workflow tests the application, builds the Store artifacts, and can either create a Partner Center draft or submit it for certification. It intentionally does not publish unsigned packages to GitHub Releases.

### Configure automated Store submission

1. In Partner Center, add a Microsoft Entra application under **Account settings > User management > Microsoft Entra applications** and grant it the Manager role required by the Store submission API.
2. Create the `microsoft-store-production` GitHub environment and configure required reviewers before allowing certification submissions.
3. Add these environment secrets:

   - `PARTNER_CENTER_TENANT_ID`
   - `PARTNER_CENTER_SELLER_ID`
   - `PARTNER_CENTER_CLIENT_ID`
   - `PARTNER_CENTER_CLIENT_SECRET`

4. Run **Build Store package** from GitHub Actions, enter a version higher than the last Store package version, and select one of these submission modes:

   - `draft` uploads the package but leaves the Partner Center submission uncommitted for manual review;
   - `certification` commits the submission and starts Microsoft Store certification after the environment approval.

The Store publishing CLI currently supports automated updates for free products. Rotate the Partner Center client secret before it expires. Certification and final publication remain asynchronous Microsoft Store processes; use Partner Center to monitor the complete certification result.

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
