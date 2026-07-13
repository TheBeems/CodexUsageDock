# Development

## Prerequisites

- Windows 10 build 19041 or newer, or Windows 11
- .NET 10 SDK
- PowerToys 0.100.0 or newer
- Windows SDK tools (`makeappx.exe` and `signtool.exe`) for packaging and signature verification
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
dotnet restore .\CodexUsageDock.sln
dotnet test .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -c Debug -p:Platform=x64
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=x64
dotnet build .\CodexUsageDock\CodexUsageDock.csproj -c Debug -p:Platform=ARM64
```

## Build release packages

`scripts/build-release.ps1` is the canonical package builder. It always restores `Package.appxmanifest`, even after a failed build, and validates the identity, version, self-contained runtime files, and bundle architectures.

Build a community-WinGet bundle with the exact subject shown by the validated Artifact Signing certificate profile:

```powershell
$publisher = '<exact Artifact Signing certificate subject>'
.\scripts\build-release.ps1 `
  -Version 0.2.1 `
  -Distribution Winget `
  -Publisher $publisher
```

The command creates x64 and ARM64 packages and the final `artifacts\winget\CodexUsageDock-0.2.1.msixbundle`. These local outputs are unsigned and must not be distributed. The release workflow signs only the final bundle and regenerates `SHA256SUMS.txt` afterward.

The Store identity can still be built for diagnostics:

```powershell
.\scripts\build-release.ps1 -Version 0.2.1 -Distribution Store
```

That command creates an unsigned bundle and `.msixupload` under `artifacts\store`. The **Build Store package (paused)** workflow is manual and build-only. It has no Partner Center credentials or publication step. Never publish these unsigned artifacts.

## Artifact Signing infrastructure

Production releases use an Azure Artifact Signing Basic account in West Europe with a `PublicTrust` certificate profile. Refer to the current [Artifact Signing quickstart](https://learn.microsoft.com/azure/artifact-signing/quickstart), [role assignment guidance](https://learn.microsoft.com/azure/artifact-signing/tutorial-assign-roles), and [signing integration guide](https://learn.microsoft.com/azure/artifact-signing/how-to-signing-integrations).

Canonical resource names:

- resource group: `rg-codexusagedock-signing`
- preferred account name: `codexusagedock`
- fallback account name: `codexusagedock-<first eight subscription-id hex characters>`
- region: `westeurope`
- SKU: `Basic`
- certificate profile: `codexusagedock-public`
- profile type: `PublicTrust`
- endpoint: `https://weu.codesigning.azure.net`

If `codexusagedock` is unavailable, check and use the deterministic fallback rather than selecting a random name:

```powershell
$suffix = ($subscriptionId -replace '-', '').Substring(0, 8).ToLowerInvariant()
$accountName = "codexusagedock-$suffix"
az artifact-signing check-name-availability `
  --type Microsoft.CodeSigning/codeSigningAccounts `
  --name $accountName
```

Register `Microsoft.CodeSigning`, create the resource group and Basic account, then complete EU organization identity validation in the Azure portal. Identity validation cannot be completed with Azure CLI. Create `codexusagedock-public` only after the validation succeeds:

```powershell
az provider register --namespace Microsoft.CodeSigning
az group create --name rg-codexusagedock-signing --location westeurope
az artifact-signing create `
  --resource-group rg-codexusagedock-signing `
  --account-name $accountName `
  --location westeurope `
  --sku Basic
az artifact-signing certificate-profile create `
  --resource-group rg-codexusagedock-signing `
  --account-name $accountName `
  --name codexusagedock-public `
  --profile-type PublicTrust `
  --identity-validation-id $identityValidationId
```

Copy the exact **Certificate Subject Preview** from the validated profile. Do not reconstruct, abbreviate, or guess the distinguished name: the MSIX manifest publisher and signing-certificate subject must match exactly.

## GitHub OIDC and least privilege

Create a Microsoft Entra application/service principal dedicated to GitHub signing. Add one federated credential with:

- issuer: `https://token.actions.githubusercontent.com`
- subject: `repo:TheBeems/CodexUsageDock:environment:winget-production`
- audience: `api://AzureADTokenExchange`

Assign only `Artifact Signing Certificate Profile Signer` to that service principal at this scope:

```text
/subscriptions/<subscription-id>/resourceGroups/rg-codexusagedock-signing/providers/Microsoft.CodeSigning/codeSigningAccounts/<account-name>/certificateProfiles/codexusagedock-public
```

Do not create a client secret. The release job has `id-token: write` only while it runs inside the protected `winget-production` environment.

Configure required reviewers on that environment and add these environment variables:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `ARTIFACT_SIGNING_ENDPOINT`
- `ARTIFACT_SIGNING_ACCOUNT_NAME`
- `ARTIFACT_SIGNING_CERTIFICATE_PROFILE`
- `ARTIFACT_SIGNING_PUBLISHER`

Add the same `ARTIFACT_SIGNING_PUBLISHER` value as a repository variable. This non-secret mirror lets the unprivileged build job create and validate the bundle before the environment grants OIDC signing access. The protected job fails when the two values differ.

The only signing-related secret is the existing `WINGET_PAT`; it is used for WinGet pull requests, not Azure authentication.

## Signed GitHub release

The **Signed MSIX Release** workflow performs these stages:

1. restore, 31 xUnit tests, and x64/ARM64 Release builds without signing access;
2. build and inspect one self-contained x64+ARM64 bundle;
3. wait for `winget-production` approval;
4. authenticate to Azure with GitHub OIDC and sign only the final bundle with the pinned official Artifact Signing action;
5. verify the public chain, RFC3161 timestamp, exact subject, version, and architectures with SignTool;
6. install the bundle on a clean x64 runner, inspect packaged COM and `com.microsoft.commandpalette` registration, and uninstall it;
7. publish exactly the signed bundle and `SHA256SUMS.txt` for version tags.

Pull requests run only stages 1 and 2 with a non-production CI publisher and one-day unsigned artifact retention. They never request the protected environment or signing permission.

The timestamp authority is `http://timestamp.acs.microsoft.com`. A workflow-dispatch run signs and uploads a temporary Actions artifact but does not create a GitHub release. A tag run refuses to replace an existing release, so published assets remain immutable.

Release `0.2.1` only after a successful workflow-dispatch rehearsal:

```powershell
git tag -a v0.2.1 -m "Release v0.2.1"
git push origin v0.2.1
```

## Initial WinGet publication

Do not construct identity-derived values by hand. After the signed `v0.2.1` asset exists, run WingetCreate against its direct URL. Inspect the generated manifest and set schema version `1.12.0` with:

- `PackageIdentifier: TheBeems.CodexUsageDock`
- `PackageVersion: 0.2.1`
- `InstallerType: msix`
- `Scope: user`
- `UpgradeBehavior: install`
- x64 and ARM64 installer nodes that use the same `.msixbundle` URL
- `PackageFamilyName`, `InstallerSha256`, and `SignatureSha256` derived from the signed bundle
- `windows-commandpalette-extension` in `Tags`
- no Windows App Runtime dependency; this project does not reference the Windows App SDK runtime package and both RID packages are self-contained

Validate the generated directory before submission:

```powershell
winget validate --manifest .\path\to\TheBeems.CodexUsageDock\0.2.1
winget install --manifest .\path\to\TheBeems.CodexUsageDock\0.2.1
winget uninstall --id TheBeems.CodexUsageDock --exact
```

Use the existing `WINGET_PAT` when WingetCreate opens the initial PR. Follow the PR through merge, refresh the public source, and verify:

```powershell
winget source update --name winget
winget show --id TheBeems.CodexUsageDock --exact --source winget
winget install --id TheBeems.CodexUsageDock --exact --source winget
winget uninstall --id TheBeems.CodexUsageDock --exact --source winget
```

Keep repository variable `WINGET_AUTOMATION_ENABLED` unset or `false` until those checks pass. Set it to `true` only after the initial PR is merged; `.github/workflows/winget-update.yml` then submits future published bundles with `wingetcreate update --submit`.

## Command Palette Gallery

After WinGet is public, open a separate pull request in `microsoft/CmdPal-Extensions` for:

```text
extensions/thebeems/codex-usage-dock/
```

Use `TheBeems.CodexUsageDock` as the install source and `CodexUsageDock/Assets/Square150x150Logo.scale-200.png` as the 300×300 icon. Validate the entry against the current [Command Palette extension Gallery requirements](https://learn.microsoft.com/windows/powertoys/command-palette/extension-gallery).

## Store identity is paused

The unchanged Store identity is:

- package name: `TheBeems.CodexUsageDock`
- publisher: `CN=F748B633-A4F0-42F4-B6F1-B5BDCAED8E0C`
- publisher display name: `TheBeems`
- Store ID: `9NFCPJXQG9FG`

That publisher differs from the Artifact Signing Public Trust subject. Store publication is paused until a separate migration plan resolves the identity conflict. Do not change the Store identity, submit a Store package, or modify release `v0.2.0` as part of the community-WinGet release.

## Architecture

- `CodexUsageDock.cs` exposes the Command Palette extension.
- `CodexUsageDockCommandsProvider.cs` provides commands and the Dock band.
- `CodexUsageService.cs` reads limits from the local Codex app-server and falls back to local session metadata.
- `Package.appxmanifest` contains the packaged COM server and Command Palette AppExtension registration.
