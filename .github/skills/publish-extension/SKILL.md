---
name: publish-extension
description: >-
  Prepare, commit, push, publish, install, or resume a Codex Usage Dock release.
  Use for this repository's GitHub and Microsoft Store release cycle, including
  continuing after certification. Follow only the stages the user requests.
---

# Publish Codex Usage Dock

Use [DEVELOPMENT.md](../../../DEVELOPMENT.md) as the canonical source for commands, architecture checks, Store identity, and installation rules. Reuse the existing scripts and workflow. Read [Store publishing](references/store-publishing.md) when submitting, installing, or resuming a release.

## Scope and authorization

- Treat commit, push/merge, Store submission, and installation as separately requestable stages. A status question or a request to commit does not authorize the full cycle.
- Reuse explicit authorization already given for this release. If the user requests the complete cycle, proceed through its stages without repeated confirmation, subject to actual tool approval requirements.
- Do not expand a release into Gallery submissions, infrastructure changes, new credentials, or scheduled monitoring. Handle those only when requested. A skill itself does not wake up later.

## Release workflow

1. **Locate the release.** Read Git status, branch, remote, project version, and the existing release record from this task. Verify relevant remote PR/build/release and Store submission state before creating anything. Keep version, source commit, and package provenance together.
2. **Prepare.** Review the intended diff and staged files for unrelated changes, generated artifacts, and sensitive data. Choose the version from current repository and Store evidence. Update the changelog and affected documentation. Reuse successful checks for unchanged inputs; run the additional tests/builds required by changed code, version, architecture, or packaging.
3. **Commit and push.** Stage the intended files explicitly. Follow repository branch and PR rules, inspect checks for the actual PR head, and merge only when required checks pass and merging is authorized. Link changelog entries to the implementing commit or PR before merging. Recheck the resulting commit and working tree.
4. **Build and submit.** Run the manual **Store package** workflow for the intended source commit. Verify success, artifact version, and checksum. Reuse a matching draft submission; upload once, verify both package architectures, update release notes in existing listing languages and testing instructions, then submit within the user's authorization.
5. **Resume after certification.** Report the exact Store phase and next action. Certification pending is a dependency, not a failure to retry by resubmitting. Continue from that phase when asked; see the reference for rejection and uncertain upload handling.
6. **Install and close.** Once the exact version is available, update through Microsoft Store and verify the installed version, package health, Command Palette discovery, and the applicable functional checks. Complete the source-only GitHub release and publication documentation as directed in `DEVELOPMENT.md`. Report any verification that could not be performed.

## Invariants and completion

- Microsoft Store is the production and update channel. Do not install unsigned Actions artifacts, introduce self-signing, or replace a production Store registration with a development build.
- Preserve the assigned Store identity, COM registration, capabilities, self-contained x64/ARM64 layout, and older releases. A change to these needs explicit task scope.
- Prefer the existing GitHub CLI/workflow and package scripts for deterministic operations; use the supported browser for Partner Center when no suitable API is configured. Follow repository escalation instructions. Never store credentials in Git or release records.
- Distinguish **committed**, **pushed/merged**, **package validated**, **submitted**, **certified**, **published**, **installed**, and **functionally verified**. Claim each only with evidence. A successful build does not prove installation; an old public Store listing does not prove the new version is available.
- Keep a compact handoff record in the task response using the reference checklist. Stop on an external dependency or concrete tool limitation with the completed stages and next action stated; do not promise background continuation unless monitoring was actually requested and configured.
