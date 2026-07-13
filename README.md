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

The Microsoft Store package includes the required .NET runtime. You do not need the .NET SDK to install or use the extension.

## Install

1. Open the [latest GitHub release](https://github.com/TheBeems/CodexUsageDock/releases/latest).
2. Download the installer for your device:
   - `x64` for most Intel and AMD Windows PCs;
   - `arm64` for Windows-on-ARM devices such as Snapdragon PCs.
3. Run the downloaded installer.
4. Reopen PowerToys Command Palette if it was already running.

The installer is per-user and does not require administrator privileges. It includes the required .NET runtime.

> [!NOTE]
> GitHub installers are not code-signed yet. Windows SmartScreen may display an **Unknown publisher** warning. Every release includes `SHA256SUMS.txt` so you can verify the download before running it.

Microsoft Store publication is also being prepared under Store ID `9NFCPJXQG9FG`.

## Add Codex Usage to the Dock

1. Open Command Palette settings.
2. Select **Extensions** and make sure **Codex Usage** is enabled.
3. Select **Dock (Preview)** and enable the Dock.
4. Open the Dock customization interface.
5. Choose **Add command** (`+`) in the section where you want the widget.
6. Search for **Codex Usage** and select its Dock band.

The Dock will show entries similar to `5h 47%`, `Week 86%`, and `↻ 2 · 10.00`. The percentages represent the amount remaining. The final entry shows available earned resets and, when available, the credits balance. Select an entry to see reset expiry details or refresh the data manually.

## Update

Download and run the installer from the newest GitHub release. It replaces the previous version while retaining the same installation location and Command Palette registration. Automatic updates will become available with the Microsoft Store release.

## Uninstall

Open **Windows Settings > Apps > Installed apps**, find **Codex Usage Dock**, and select **Uninstall**.

## Troubleshooting

- Confirm that a standalone `codex.exe` or `codex.cmd` is available on `PATH`, or set `CODEX_USAGE_DOCK_CODEX_PATH` to its full path and restart PowerToys. The extension will show local fallback data when no launchable CLI is found.
- Confirm that Codex is signed in.
- Confirm that PowerToys Command Palette is enabled and running.
- Confirm that PowerToys is version 0.100.0 or newer.
- If the extension does not appear, close and reopen Command Palette and check **Settings > Extensions**.
- If usage cannot be loaded, start Codex once so local account and session metadata are available.

## Privacy

The extension runs locally. It talks to the locally installed Codex app-server and may read local Codex session metadata for its fallback path. It does not send usage information to a separate service.

See the full [Privacy Policy](PRIVACY.md).

## Development

Build, test, packaging, and release instructions are in [DEVELOPMENT.md](DEVELOPMENT.md).

## License

Codex Usage Dock is available under the [MIT License](LICENSE).
