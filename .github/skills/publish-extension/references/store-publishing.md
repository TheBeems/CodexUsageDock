# Store publication and resuming a release

Read the relevant sections of [DEVELOPMENT.md](../../../../DEVELOPMENT.md) for exact commands and identity. Keep one authoritative release record in the task; do not create a separate release database or copy the full runbook into the skill.

## Evidence to carry between stages

Record only values actually observed; use "not yet verified" for missing evidence:

- Intended version and repository/remote.
- Source commit, PR URL and merge commit, if applicable.
- Successful workflow run URL and its source SHA.
- Artifact name, local download path, and verified SHA256.
- Partner Center product and submission IDs/URL; uploaded package version and architectures.
- Last observed Store phase and observation time; publishing hold setting.
- Installed version/architecture, package health, and functional checks performed.
- Remaining action, dependency, and the stages already authorized by the user.

Local paths belong in the task record, not committed documentation. Omit account identifiers, credentials, cookies, and session tokens. On a later request, use this record to target current checks; do not treat its old status as current.

## GitHub and package provenance

- Look for an existing matching PR, workflow run, tag, and GitHub release before creating another. A successful older run is insufficient if release inputs changed.
- Inspect required checks for the exact PR head before merging. Select the manual workflow run by its source SHA as well as its version; a filename alone does not establish provenance.
- Download the workflow artifact, verify `SHA256SUMS.txt`, and retain the `.msixupload` unchanged. The canonical builder validates the self-contained x64 and ARM64 packages and leaves the source manifest unchanged.
- Wait for the intended version to be publicly available before tagging and publishing its source-only GitHub release. Do not attach MSIX files or rewrite an existing release/tag to resolve a mismatch.

## Partner Center

- Open the existing product identified in the runbook. Check its current submission first. Resume a matching draft; if an unrelated draft or submission is in progress, preserve it and clarify the conflict.
- Preserve pricing, markets, permissions, listing assets, and unrelated product settings. Update the release notes in the existing listing languages. Record whether publication is automatic after certification or held; follow the user's requested timing.
- Use the supported file-chooser upload flow. After a timeout or slow response, inspect the current upload/submission status before retrying. Check package version and both architectures after validation, save, and confirm that the overview lists the new package as validated.
- Testing notes must explain Command Palette activation, the absence of a standalone Start-menu entry, prerequisites, and the key changed behavior. Never supply invented test results or credentials.
- Submit only the validated intended version. Confirm the resulting submission phase; the presence of a Submit button alone is not submission evidence.

## Waiting, failure, and installation

| Observed state | Next action |
| --- | --- |
| Upload response uncertain | Inspect the same submission for progress or the uploaded filename/version before retrying. |
| Draft with intended package validated | Complete outstanding fields and submit if authorized. |
| Pre-processing or certification | Record the pending dependency. Do not create another submission. |
| Rejected | Read and report the stated reason. Correct the issue in a new package version within authorized scope; do not bypass Store distribution. |
| Certified but publishing held | Release the hold only when publication is authorized for the requested timing. |
| Publishing | Wait for the exact version to become available, rather than treating the old product listing as proof. |
| New version available | Perform the authorized Store update, then check installed version and health. |
| Installed | Verify activation and the relevant behavior; report any manual gaps. |

For settings changes, verify that choices survive an extension restart and, where feasible, a Windows restart. Do not reboot the user's computer unexpectedly; arrange disruptive verification with them. Successful unit tests of settings reload are evidence of serialization behavior, not an actual machine restart.

An unavailable browser login, Store certification, or delayed rollout may require user input or an external state change. State that dependency without reporting the release as fully installed. Configure future monitoring only when requested; retain the release record so the next run can resume directly.
