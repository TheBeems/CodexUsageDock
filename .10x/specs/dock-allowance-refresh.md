Status: active
Created: 2026-07-20
Updated: 2026-07-20

# Dock allowance refresh contract

## Scope

The Dock and the usage details page must present the same latest allowance snapshot after a completed `CodexUsageService` refresh.

## Contract

Given a completed refresh publishes a weekly or five-hour allowance value, when Command Palette is displaying the Dock, then the provider MUST invalidate the Dock-band collection so that the host requests a band rendered from that completed snapshot.

The Dock MAY show the loading state while a refresh is in progress, but it MUST NOT retain a prior completed allowance after the details page has rendered the newer completed snapshot.

## Acceptance criteria

- A completed refresh causes a provider-level item invalidation in addition to any child-list notification.
- The Dock band returned after that invalidation contains the current snapshot's formatted allowance.
- The details page remains driven by the same `CodexUsageService.Current` snapshot.

## Exclusions

- No change to allowance calculation, refresh cadence, rate-limit source selection, or visual layout.
