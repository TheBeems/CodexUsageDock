# Reproduce missing footer actions after Back

## Purpose and limits

The reported behavior is that returning from **Usage information** or **Read usage in text** hides the dashboard footer actions; returning from **Settings** can hide only Settings. This test separates extension command state from the PowerToys host's navigation and rendering.

Debug builds expose **Codex Usage navigation test**. Its dashboard and settings use `FormContent`, and its information and text pages use `MarkdownContent`, matching the production page types. The dashboard has four stable commands. The children have zero, two, and one commands respectively. Child actions have distinctive names to reveal a stale footer. The fixture has no service, timer, file access, settings persistence, or clipboard operations. The normal extension provider still starts its usual usage service when loaded.

The fixture deliberately does not republish `Commands` on Refresh. A refresh changes only an in-memory counter and raises the content notification, as the real dashboard does. This is a diagnostic fixture, not a navigation fix.

## Setup

1. Build and install the Debug package using the authorized [local test workflow](../DEVELOPMENT.md#test-in-your-current-windows-account).
2. Run **Reload Command Palette Extension**. Search for **Codex Usage navigation test** in Command Palette and open it.
3. Record PowerToys and Command Palette versions, architecture, build configuration, source revision (including uncommitted changes), display scaling, language, and window size. Keep the size constant during the test.
4. Capture the baseline dashboard, including its title and footer. At normal width, expect **Refresh test dashboard**, **Settings**, and **More** (localized by the host). More must contain **Usage information** and **Read usage in text**. Record all menu entries; the host can also list the visible commands there.

No Codex account data is needed by the fixture. Do not invoke the real settings page's history-deletion action during the production comparison below.

## Host UI test

Start each route from a freshly opened test dashboard with the expected baseline footer. Use the host's visible Back arrow, not a command that opens another dashboard instance.

| Route | Child page check | Expected immediately after Back |
| --- | --- | --- |
| More > Usage information | Information text; no child footer commands | Dashboard title and all baseline actions return |
| More > Read usage in text | Footer has **Child refresh** and **Child copy (no clipboard)** | Dashboard actions return; neither child action remains |
| Settings | Test toggle and **Child settings action** | Dashboard actions return, including Settings; child action disappears |

For each route:

1. Open the child and capture its title/footer.
2. Click Back, wait one second without other input, then capture the entire dashboard and footer **before** refreshing or reopening anything.
3. If actions are missing, press **Ctrl+K** and capture whether the menu still exposes them. Close the menu. Record whether opening the menu restored the footer.
4. If accessible, invoke **Refresh test dashboard**. The counter must increment. Record separately whether refresh restores the footer; this is recovery evidence, not a passing Back result.
5. Reopen the fixture and repeat twice more. Then run all three routes in sequence without reopening between routes, if the actions remain accessible.
6. Repeat using Escape as the host's back gesture. Record if Escape closes the panel rather than navigating Back; do not treat closing/reopening as successful Back navigation.

Run the same routes on the normal **Codex Usage** dashboard opened from the Dock. Also compare opening that dashboard from Command Palette search. Record the entry point for each result. This comparison includes real background updates and distinguishes a generic ContentPage issue from a Dock-only or service-update issue. The fixture itself does not simulate automatic updates.

## Record results

Copy this table into a local test note; screenshots can go under ignored `artifacts/audit/`. Avoid account identifiers or usage details in any externally shared evidence; prefer the synthetic fixture.

| Page / entry point | Route | Back gesture | Cycle | Expected actions after Back | Observed actions / menu | Screenshot | Result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Fixture / search | Information | Arrow | 1 | Refresh, Settings, More | Pending | Pending | Not run |
| Fixture / search | Text | Arrow | 1 | Refresh, Settings, More | Pending | Pending | Not run |
| Fixture / search | Settings | Arrow | 1 | Refresh, Settings, More | Pending | Pending | Not run |

**Pass:** after Back, all baseline dashboard actions are present and target the correct page/action, with no stale child commands and no recovery refresh required. **Fail:** any missing or stale action. A successful unit test or build does not fill in a host UI result.

## Automated contract checks

Use a matching platform/RID from [Verify the application](../DEVELOPMENT.md#verify-the-application), for example on ARM64:

```powershell
dotnet test .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -c Debug -p:Platform=ARM64 -r win-arm64 -p:SelfContained=true --filter FullyQualifiedName~NavigationContractTests
dotnet test .\CodexUsageDock.Tests\CodexUsageDock.Tests.csproj -c Release -p:Platform=ARM64 -r win-arm64 -p:SelfContained=true --filter FullyQualifiedName~NavigationContractTests
```

These check that the real dashboard keeps the same command items, targets, and order across child-content reads and presentation refreshes; that the fixture's page shapes and refresh counter work; and that Release builds contain neither the fixture command nor its page type. Calling `GetContent()` is not host navigation. These tests should pass even when the PowerToys UI bug is present.

If the fixture reproduces the failure while contract tests pass, investigate host command-bar restoration on ContentPage Back navigation. If only production reproduces it, investigate background content/command notifications and the Dock entry point before choosing a fix.
