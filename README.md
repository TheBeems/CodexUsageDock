# Codex Usage Dock

Codex Usage Dock is a Windows Command Palette extension that shows your Codex limits directly in the PowerToys Dock.

It displays:

- the percentage remaining in the rolling five-hour usage window;
- the percentage remaining in the weekly usage window;
- compact pace indicators that compare allowance used with elapsed window time;
- a projected allowance at reset, or an estimated limit time when current consumption would exhaust it sooner;
- an optional adaptive weekly forecast that keeps the current pace dominant and gradually blends in up to eight local quota cycles and six-hour usage patterns;
- a weekly trend chart with sampled remaining allowance, a dashed projection that can follow six-hour adaptive forecast points, equal-width local calendar-day columns, dated weekday labels, partial reset-boundary days, and a shared 0–100% vertical scale; the line connects samples across measurement gaps but breaks at allowance increases, while the forecast uses only the latest post-increase segment;
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

Microsoft Store publication for version `0.5.0` is currently pending. That version remains the active Store submission and latest GitHub release; later versioned changes in this repository have not replaced it as the supported production candidate. Once certification is complete, the Store listing will be linked here as the only supported production installer.

GitHub Actions artifacts are inputs for Microsoft Store certification, not public installers. Do not distribute or install them directly.

Codex Usage Dock is activated inside PowerToys Command Palette and intentionally has no standalone Start-menu entry.

## Add Codex Usage to the Dock

1. Open Command Palette settings.
2. Select **Extensions** and make sure **Codex Usage** is enabled.
3. Select **Dock (Preview)** and enable the Dock.
4. Open the Dock customization interface.
5. Choose **Add command** (`+`) in the section where you want the widget.
6. Search for **Codex Usage** and select its Dock band.

The Dock will show entries similar to `5h 47%`, `Week 86%`, and `2 resets · 10.00`. The percentages represent the amount remaining. The final entry shows available earned resets, the time until the next reset credit expires in whole hours or days, and, when available, the credits balance. Select an entry to see reset expiry details or refresh the data manually.

## Customize the Dock

Open Command Palette and select **Codex Usage settings** to choose which usage entries appear in the Dock. You can independently show or hide the five-hour limit, weekly limit, and resets and credits, choose whether usage entries show their reset time, set the local data refresh interval to 1, 5, or 15 minutes, and enable or pause the adaptive weekly forecast. Pausing the forecast keeps its learned local history; **Delete learned forecast history** asks for confirmation before permanently clearing it. Command Palette stores these settings for the current user.

## Update

Microsoft Store installs updates automatically. You can also check for updates from **Microsoft Store > Library**.

## Uninstall

Remove **Codex Usage Dock** from **Windows Settings > Apps > Installed apps**.

## Troubleshooting

- Confirm that a standalone `codex.exe` or `codex.cmd` is available on `PATH`, or set `CODEX_USAGE_DOCK_CODEX_PATH` to its full path and restart PowerToys. The extension will show local fallback data when no launchable CLI is found.
- Confirm that Codex is signed in.
- Confirm that PowerToys Command Palette is enabled and running.
- Confirm that PowerToys is version 0.100.0 or newer.
- If the extension does not appear, open Command Palette, run **Reload Command Palette Extension**, and then confirm that **Codex Usage** is enabled under **Settings > Extensions**.
- If usage cannot be loaded, start Codex once so local account and session metadata are available.

## Distribution status

Microsoft Store product `9NFCPJXQG9FG` is the only production and update channel starting with version `0.3.0`. Publication of version `0.5.0` is currently awaiting Store certification, so later versioned repository changes are not yet separate production releases. The GitHub `v0.5.0` release records the current source release and does not contain an unsigned installer.

## Privacy

The extension runs locally. It talks to the standalone Codex CLI app-server and may read local Codex session metadata for its fallback path. It keeps a rolling seven-day weekly usage trend and up to eight aggregated local weekly forecast profiles on the device, and does not send usage information to a separate service.

See the full [Privacy Policy](PRIVACY.md).

## Development

Build, test, Store packaging, and release instructions are in [DEVELOPMENT.md](DEVELOPMENT.md).

## License

Codex Usage Dock is available under the [MIT License](LICENSE).
