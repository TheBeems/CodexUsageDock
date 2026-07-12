# Codex Usage Dock

Codex Usage Dock is a Windows Command Palette extension that shows your Codex limits directly in the PowerToys Dock.

It displays:

- the percentage remaining in the rolling five-hour usage window;
- the percentage remaining in the weekly usage window;
- the Codex connection status;
- reset times in the item details.

The values refresh once per minute. The extension reads them from the local Codex app-server and uses local Codex session metadata as a fallback.

## Requirements

- Windows 10 build 19041 or newer, or Windows 11
- [PowerToys 0.100.0 or newer](https://github.com/microsoft/PowerToys) with Command Palette enabled
- Codex installed and signed in
- `codex` available on `PATH`

The release installer includes the required .NET runtime. You do not need the .NET SDK to install or use the extension.

## Install

1. Open the [latest release](https://github.com/TheBeems/CodexUsageDock/releases/latest).
2. Download the installer for your device:
   - `x64` for most Intel and AMD Windows PCs;
   - `arm64` for Windows-on-ARM devices such as Snapdragon PCs.
3. Run the downloaded installer.
4. Reopen PowerToys Command Palette if it was running during installation.

The installer is per-user and does not require administrator privileges.

> [!NOTE]
> Phase-one GitHub installers are not yet code-signed. Windows SmartScreen may display an **Unknown publisher** warning. Release assets include `SHA256SUMS.txt` so you can verify the download before running it.

## Add Codex Usage to the Dock

1. Open Command Palette settings.
2. Select **Extensions** and make sure **Codex Usage** is enabled.
3. Select **Dock (Preview)** and enable the Dock.
4. Open the Dock customization interface.
5. Choose **Add command** (`+`) in the section where you want the widget.
6. Search for **Codex Usage** and select its Dock band.

The Dock will show entries similar to `5h 47%`, `Week 86%`, and `Codex ✓`. The percentages represent the amount remaining. Select an entry to see reset details or refresh the data manually.

## Update

Download and run the installer from the newest GitHub Release. It replaces the previous version while retaining the same installation location and Command Palette registration.

Automated updates through WinGet are planned for the next distribution phase.

## Uninstall

Open **Windows Settings > Apps > Installed apps**, find **Codex Usage Dock**, and select **Uninstall**.

## Troubleshooting

- Confirm that `codex --version` works in a new PowerShell window.
- Confirm that Codex is signed in.
- Confirm that PowerToys Command Palette is enabled and running.
- Confirm that PowerToys is version 0.100.0 or newer; earlier Command Palette versions do not discover unpackaged release installers.
- If the extension does not appear, close and reopen Command Palette and check **Settings > Extensions**.
- If usage cannot be loaded, start Codex once so local account and session metadata are available.

## Privacy

The extension runs locally. It talks to the locally installed Codex app-server and may read local Codex session metadata for its fallback path. It does not send usage information to a separate service.

## Development

Build, test, packaging, and release instructions are in [DEVELOPMENT.md](DEVELOPMENT.md).

## License

Codex Usage Dock is available under the [MIT License](LICENSE).
