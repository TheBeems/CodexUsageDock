---
name: publish-extension
description: >-
  Build Codex Usage Dock for manual Microsoft Store publication and submit its
  listing to the Command Palette Gallery after Store certification.
---

# Publish Codex Usage Dock

Microsoft Store is the only production and update channel. Read `DEVELOPMENT.md` before changing packaging or release infrastructure; it is the canonical release runbook.

## Required release shape

- Build with `scripts/build-release.ps1 -Version <three-part-version>`.
- Preserve the assigned Store identity and self-contained x64 and ARM64 packages in one `.msixupload`.
- Download the package from the manual **Store package** workflow and upload it to existing Partner Center product `9NFCPJXQG9FG`.
- Let Microsoft Store sign and distribute the certified package.
- Create a source-only GitHub Release after Store publication; never attach package files.
- Open the Command Palette Gallery pull request only after public Store installation succeeds.

## Safety rules

- Never distribute Actions artifacts or local Store outputs as installers.
- Never use a self-signed, GitHub asset, or alternate distribution fallback.
- Never add signing or Partner Center credentials to GitHub.
- Never change the package name, publisher, publisher display name, or Store product ID.
- Do not modify release `v0.2.0`.

See [Store publishing](references/store-publishing.md) for the handoff checklist and `DEVELOPMENT.md` for the canonical commands.
