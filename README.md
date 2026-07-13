# Codex Usage Dock

Codex Usage Dock is a Windows Command Palette extension that shows your Codex limits directly in the PowerToys Dock.

It displays:

- the percentage remaining in the rolling five-hour usage window;
- the percentage remaining in the weekly usage window;
- the number of available earned resets and their expiry times;
- the remaining credits balance when Codex provides it.

The values refresh once per minute. The extension reads them from the local Codex app-server and uses local Codex session metadata as a fallback.

## Requirements

- Windows 10 build 19041 or newer, or Windows 11
- [PowerToys 0.100.0 or newer](https://github.com/microsoft/PowerToys) with Command Palette enabled
- Codex installed and signed in
- A standalone `codex.exe` or `codex.cmd` CLI available on `PATH`, or `CODEX_USAGE_DOCK_CODEX_PATH` set to its full path. The protected CLI bundled with the Microsoft Store Codex Desktop app cannot be launched by this extension.

The package includes the required .NET runtime. You do not need the .NET SDK to install or use the extension.

## Install

Install the signed x64 or ARM64 package from the WinGet community source:

```powershell
winget install --id TheBeems.CodexUsageDock --exact --source winget
```

Reopen PowerToys Command Palette if it was already running. The MSIX package is installed per user and does not require administrator privileges.

The signed `.msixbundle` and `SHA256SUMS.txt` are also available on the [latest GitHub release](https://github.com/TheBeems/CodexUsageDock/releases/latest). Use the direct bundle only when the WinGet community index is temporarily unavailable.

## Add Codex Usage to the Dock

1. Open Command Palette settings.
2. Select **Extensions** and make sure **Codex Usage** is enabled.
3. Select **Dock (Preview)** and enable the Dock.
4. Open the Dock customization interface.
5. Choose **Add command** (`+`) in the section where you want the widget.
6. Search for **Codex Usage** and select its Dock band.

The Dock will show entries similar to `5h 47%`, `Week 86%`, and `↻ 2 · 10.00`. The percentages represent the amount remaining. The final entry shows available earned resets and, when available, the credits balance. Select an entry to see reset expiry details or refresh the data manually.

## Update

```powershell
winget upgrade --id TheBeems.CodexUsageDock --exact --source winget
```

## Uninstall

```powershell
winget uninstall --id TheBeems.CodexUsageDock --exact --source winget
```

You can also remove **Codex Usage Dock** from **Windows Settings > Apps > Installed apps**.

## Troubleshooting

- Confirm that a standalone `codex.exe` or `codex.cmd` is available on `PATH`, or set `CODEX_USAGE_DOCK_CODEX_PATH` to its full path and restart PowerToys. The extension will show local fallback data when no launchable CLI is found.
- Confirm that Codex is signed in.
- Confirm that PowerToys Command Palette is enabled and running.
- Confirm that PowerToys is version 0.100.0 or newer.
- If the extension does not appear, close and reopen Command Palette and check **Settings > Extensions**.
- If usage cannot be loaded, start Codex once so local account and session metadata are available.

## Distribution status

Community WinGet is the canonical production channel starting with version `0.2.1`. Microsoft Store publication is paused because Store product `9NFCPJXQG9FG` has a different publisher identity. Store submission will not resume until an explicit identity-migration plan is approved. Release `v0.2.0` remains unchanged.

## Privacy

The extension runs locally. It talks to the locally installed Codex app-server and may read local Codex session metadata for its fallback path. It does not send usage information to a separate service.

See the full [Privacy Policy](PRIVACY.md).

## Development

Build, test, signing, packaging, and release instructions are in [DEVELOPMENT.md](DEVELOPMENT.md).

## License

Codex Usage Dock is available under the [MIT License](LICENSE).
