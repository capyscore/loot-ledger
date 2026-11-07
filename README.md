# Loot Ledger

**A lightweight chest tracker for Fortnite Battle Royale and Zero Build.**

A click-through Windows overlay that tracks chests opened each match and keeps your all-time total.

## Features

- **Automatic counting** — reads chest XP notices from the English HUD.
- **Match & lifetime totals** — resets each match and saves your progress.
- **Custom placement** — four overlay corners, desktop preview, and automatic hiding when you switch apps.
- **Chest hotkey** — manually count missed or broken chests with a shortcut you choose.
- **Reset controls** — clear the current match or both counters directly from the tray menu.
- **Windows installer** — choose an install folder, create a desktop shortcut, and optionally start with Windows.
- **Local & private** — no account, API key, or telemetry.

## Getting started

1. Download **LootLedger-Setup.exe** from [Releases](../../releases/latest).
2. Run setup, choose your install folder, and select desktop shortcut and Windows startup options.
3. Set Fortnite to **Windowed Fullscreen** with an **English HUD**.
4. Join a match and start opening chests.

Right-click the gold chest icon in your system tray to change settings, preview the overlay, or exit.

Startup is optional and runs when you sign in to Windows. Rerun setup to change shortcut or startup options. Uninstall through Windows **Installed apps**; your saved counts and preferences are retained. A standalone **LootLedger.exe** and ZIP are also available for portable use.

## Controls

| Tray option | What it does |
| --- | --- |
| **Position** | Choose and save an overlay corner. |
| **Enable chest hotkey** | Count a chest manually during an active match. Default: **Caps Lock**. |
| **Chest hotkey** | Press a new key or combination, then click **Save**. |
| **Reset counter → Current match** | Immediately clear the match counter; keep your all-time total. |
| **Reset counter → Match + all-time totals** | Immediately clear both counters. |

## Requirements

- 64-bit Windows 10 or 11 with **Windows PowerShell 5.1**.
- Windows **English (United States) OCR** language pack.

The EXE saves counts and settings in `%LOCALAPPDATA%\LootLedger\.loot-ledger`. Screenshots are processed in memory. Recognition can miss unreadable or suppressed chest notices; use the hotkey to fill in gaps.

## Building & releases

[Windows build](../../actions/workflows/windows.yml) tests and builds every push to `main` and every pull request. Download **LootLedger-win-x64** from a successful run, or select **Run workflow** for a manual build.

To publish, push a tag such as `v1.0.0`. CI attaches the installer, standalone EXE, ZIP, and SHA-256 checksums to a GitHub release. Publishing a release in GitHub also triggers the build. No extra secrets are needed.

For a local build, run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build\Build.ps1`. To run from source, use **Start Loot Ledger.cmd**; source runs keep saves in `.loot-ledger/` beside the launcher.

The build downloads a pinned, SHA-256-verified [Inno Setup](https://jrsoftware.org/isinfo.php) compiler into `obj/tools` in portable mode. For an offline build, pass `-InnoCompiler 'C:\path\to\ISCC.exe'` (Inno Setup 6.3 or newer). Outputs are written to `dist/`.

## License

[MIT](LICENSE) · Independent project, not affiliated with or endorsed by Epic Games.
