# Codex Usage Dock

Codex Usage Dock is a Windows Command Palette extension that shows your Codex limits directly in the PowerToys Dock.

It displays:

- the percentage remaining in the rolling five-hour usage window;
- the percentage remaining in the weekly usage window;
- the number of available earned resets and their expiry times;
- the remaining credits balance when Codex provides it.

The values refresh once per minute. The extension reads live data from the standalone Codex CLI app-server and uses local Codex session metadata as a fallback. The details page identifies the source: the live route is explicitly the CLI app-server; session metadata may have been written by the desktop app, CLI, or another local Codex client and cannot be attributed more precisely.

## Requirements

- Windows 10 build 19041 or newer, or Windows 11
- [PowerToys 0.100.0 or newer](https://github.com/microsoft/PowerToys) with Command Palette enabled
- Codex installed and signed in
- A standalone `codex.exe` or `codex.cmd` CLI available on `PATH`, or `CODEX_USAGE_DOCK_CODEX_PATH` set to its full path. The protected CLI bundled with the Microsoft Store Codex Desktop app cannot be launched by this extension.

The package includes the required .NET runtime. You do not need the .NET SDK to install or use the extension.

## Install

Microsoft Store publication for version `0.2.1` is currently pending. Once certification is complete, the Store listing will be linked here as the only supported production installer.

GitHub Actions artifacts are inputs for Microsoft Store certification, not public installers. Do not distribute or install them directly.

## Add Codex Usage to the Dock

1. Open Command Palette settings.
2. Select **Extensions** and make sure **Codex Usage** is enabled.
3. Select **Dock (Preview)** and enable the Dock.
4. Open the Dock customization interface.
5. Choose **Add command** (`+`) in the section where you want the widget.
6. Search for **Codex Usage** and select its Dock band.

The Dock will show entries similar to `5h 47%`, `Week 86%`, and `↻ 2 · 10.00`. The percentages represent the amount remaining. The final entry shows available earned resets and, when available, the credits balance. Select an entry to see reset expiry details or refresh the data manually.

## Customize the Dock

Open Command Palette and select **Codex Usage settings** to choose which usage entries appear in the Dock. You can independently show or hide the five-hour limit, weekly limit, and resets and credits, choose whether usage entries show their reset time, and set the local data refresh interval to 1, 5, or 15 minutes. Command Palette stores these settings for the current user.

## Update

Microsoft Store installs updates automatically. You can also check for updates from **Microsoft Store > Library**.

## Uninstall

Remove **Codex Usage Dock** from **Windows Settings > Apps > Installed apps**.

## Troubleshooting

- Confirm that a standalone `codex.exe` or `codex.cmd` is available on `PATH`, or set `CODEX_USAGE_DOCK_CODEX_PATH` to its full path and restart PowerToys. The extension will show local fallback data when no launchable CLI is found.
- Confirm that Codex is signed in.
- Confirm that PowerToys Command Palette is enabled and running.
- Confirm that PowerToys is version 0.100.0 or newer.
- If the extension does not appear, close and reopen Command Palette and check **Settings > Extensions**.
- If usage cannot be loaded, start Codex once so local account and session metadata are available.

## Distribution status

Microsoft Store product `9NFCPJXQG9FG` is the only production and update channel starting with version `0.2.1`. Publication is currently awaiting Store certification. Release `v0.2.0` remains unchanged.

## Privacy

The extension runs locally. It talks to the standalone Codex CLI app-server and may read local Codex session metadata for its fallback path. It does not send usage information to a separate service.

See the full [Privacy Policy](PRIVACY.md).

## Development

Build, test, Store packaging, and release instructions are in [DEVELOPMENT.md](DEVELOPMENT.md).

## License

Codex Usage Dock is available under the [MIT License](LICENSE).
