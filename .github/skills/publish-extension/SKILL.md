---
name: publish-extension
description: >-
  Publish Codex Usage Dock as a publicly trusted, signed MSIX bundle through
  GitHub Releases, community WinGet, and the Command Palette Gallery.
---

# Publish Codex Usage Dock

Community WinGet is the only production distribution path. Read `DEVELOPMENT.md` before changing packaging or release infrastructure; it is the canonical release runbook.

## Required release shape

- Build with `scripts/build-release.ps1 -Distribution Winget -Publisher <exact-subject>`.
- Preserve self-contained `win-x64` and `win-arm64` packages in one bundle.
- Sign only the final `.msixbundle` with Azure Artifact Signing Public Trust and the Microsoft RFC3161 timestamp service.
- Require an exact match between the manifest publisher and certificate subject.
- Publish only the signed bundle and `SHA256SUMS.txt`.
- Submit the initial package as `TheBeems.CodexUsageDock`; use `wingetcreate update --submit` only after the initial package is public.
- Open the Command Palette Gallery pull request only after public WinGet installation succeeds.

## Safety rules

- Never publish unsigned MSIX artifacts.
- Never use a self-signed fallback.
- Never add a client secret for Azure; use the `winget-production` GitHub environment and OIDC.
- Never guess `PackageFamilyName`, `InstallerSha256`, `SignatureSha256`, or publisher DN. Derive them from the final signed bundle/profile.
- Keep `WINGET_AUTOMATION_ENABLED` disabled until the first WinGet pull request is merged and publicly verified.
- Do not modify release `v0.2.0`.
- Do not submit to Partner Center. Store publication is paused until an explicit identity-migration plan exists.

See [WinGet publishing](references/winget-publishing.md) for the acceptance checklist and `DEVELOPMENT.md` for commands and infrastructure details.
