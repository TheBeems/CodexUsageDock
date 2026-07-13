# WinGet publishing checklist

Use the signed `CodexUsageDock-<version>.msixbundle` release asset. Unpackaged EXE/Inno installers are unsupported because Command Palette discovers this extension through packaged `com.microsoft.commandpalette` registration.

Before release:

- run all 31 xUnit tests;
- build Debug and Release for x64 and ARM64;
- confirm the bundle has both architectures and the exact validated Artifact Signing publisher;
- require approval for the `winget-production` environment;
- sign with the pinned official Artifact Signing action and `http://timestamp.acs.microsoft.com`;
- verify SignTool trust, timestamp, publisher, version, packaged COM, AppExtension registration, installation, and removal.

The GitHub release must contain only:

- `CodexUsageDock-<version>.msixbundle`
- `SHA256SUMS.txt`

The initial WinGet manifest uses `TheBeems.CodexUsageDock`, schema `1.12.0`, `InstallerType: msix`, `Scope: user`, and `UpgradeBehavior: install`. It has x64 and ARM64 installer nodes pointing to the same bundle URL. Derive `PackageFamilyName`, `InstallerSha256`, and `SignatureSha256` from the signed bundle and include the `windows-commandpalette-extension` tag. Do not add a Windows App Runtime dependency.

Run `winget validate`, local manifest install, and uninstall before using `WINGET_PAT` to submit. After merge, verify `winget show`, `install`, and `uninstall` against `--source winget` before enabling `WINGET_AUTOMATION_ENABLED`.

Then submit `extensions/thebeems/codex-usage-dock/` to the Command Palette Gallery with `TheBeems.CodexUsageDock` as its install source and `CodexUsageDock/Assets/Square150x150Logo.scale-200.png` as its 300×300 icon.

See `DEVELOPMENT.md` for the complete runbook.
