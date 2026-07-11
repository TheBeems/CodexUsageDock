# Codex Usage Dock

A Windows Command Palette extension that displays your current Codex usage directly in the PowerToys Command Palette Dock.

The first version shows:

- Remaining percentage in the rolling five-hour usage window
- Remaining percentage in the weekly usage window
- Codex connection status
- Reset times in the item details

The extension refreshes once per minute. It reads the current limits from the local Codex app-server and falls back to local Codex session data when necessary.

## Requirements

- Windows 10 version 2004 (build 19041) or newer
- PowerToys with Command Palette and Dock support
- The Codex CLI installed and signed in (`codex` must be available on `PATH`)
- .NET 10 SDK
- Windows Developer Mode enabled for local package deployment

## Build and install

Clone the repository and open PowerShell in its root directory.

Choose the platform that matches your Windows device. Most Intel and AMD PCs use `x64`; Snapdragon Windows devices use `ARM64`.

```powershell
dotnet restore CodexUsageDock.sln -p:Platform=ARM64
dotnet build CodexUsageDock.sln -c Debug -p:Platform=ARM64 --no-restore
```

For an x64 PC, replace `ARM64` with `x64` in both commands.

Register the unpackaged development build for your platform:

```powershell
Add-AppxPackage -Register .\CodexUsageDock\bin\ARM64\Debug\net10.0-windows10.0.26100.0\win-arm64\AppxManifest.xml
```

For x64, use the corresponding `bin\x64` and `win-x64` output directories. If the exact framework output folder differs after a future SDK update, locate the generated `AppxManifest.xml` under `CodexUsageDock\bin` and register that file.

## Enable the extension

1. Open PowerToys Command Palette.
2. Open **Settings**.
3. Select **Extensions**.
4. Find **Codex Usage** and enable it.

Restart Command Palette if the extension does not appear immediately.

## Add it to the Dock

1. Open Command Palette settings.
2. Select **Dock (Preview)** and enable the Dock.
3. Open the Dock customization interface.
4. Choose **Add command** (or the `+` button) in the section where you want the widget.
5. Search for and select **Codex Usage**.
6. Select the **Codex Usage** Dock band.

The Dock should now display entries similar to `5h 47%`, `Week 86%`, and `Codex ✓`. The percentages represent the amount remaining. Select an entry to view reset details or refresh the data manually.

## Troubleshooting

- Confirm that `codex --version` works in a new PowerShell window.
- Confirm that Codex is signed in and has local session data.
- Check that **Codex Usage** is enabled under Command Palette **Settings > Extensions**.
- If rebuilding fails because a file is locked by `CodexUsageDock.exe`, close Command Palette or stop the running extension, rebuild, and reopen Command Palette.
- If registration fails, enable Windows Developer Mode and run the registration command again.

## Privacy

The extension runs locally. It communicates with the locally installed Codex app-server and reads local Codex session metadata for its fallback path. It does not send usage information to a separate service.

## Development

The extension targets both x64 and ARM64. The main implementation is in:

- `CodexUsageService.cs` — app-server communication and session fallback
- `UsageDockItem.cs` — Dock labels and details
- `CodexUsageDockCommandsProvider.cs` — Command Palette provider and Dock band

## License

No license has been selected yet. All rights are reserved by the repository owner unless a license is added later.
