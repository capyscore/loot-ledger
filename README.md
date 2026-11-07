# Loot Ledger

**A lightweight chest tracker for Fortnite Battle Royale and Zero Build.**

A click-through Windows overlay that tracks chests opened each match and keeps your all-time total.

## Features

- **Automatic counting** — reads chest XP notices from the English HUD.
- **Match & lifetime totals** — resets each match and saves your progress.
- **Custom placement** — four overlay corners, desktop preview, and automatic hiding when you switch apps.
- **Chest hotkey** — manually count missed or broken chests with a shortcut you choose.
- **Optional sound detection** — recognize chest-opening audio using your own WAV sample.
- **Local & private** — no account, API key, or telemetry.

## Getting started

1. Download **LootLedger.exe** from [Releases](../../releases/latest).
2. Double-click the EXE. No installation needed.
3. Set Fortnite to **Windowed Fullscreen** with an **English HUD**.
4. Join a match and start opening chests.

Right-click the gold chest icon in your system tray to change settings, preview the overlay, or exit.

## Controls

| Tray option | What it does |
| --- | --- |
| **Position** | Choose and save an overlay corner. |
| **Enable chest hotkey** | Count a chest manually during an active match. Default: **Caps Lock**. |
| **Chest hotkey** | Press a new key or combination, then click **Save**. |
| **Tracker status...** | View totals and detection status. |

For sound detection, use **Load chest sound WAV...**, then enable **Count unconfirmed sound matches**. Use a clean, 16-bit PCM chest-opening clip, 0.25–2 seconds long. Nearby players' chest sounds may also count.

## Requirements

- 64-bit Windows 10 or 11 with **Windows PowerShell 5.1**.
- Windows **English (United States) OCR** language pack.
- Windows build **20348+** for optional sound detection.

The EXE saves counts and settings in `%LOCALAPPDATA%\LootLedger\.loot-ledger`. Screenshots are processed in memory. Recognition can miss unreadable or suppressed chest notices; use the hotkey to fill in gaps.

## Building & releases

[Windows build](../../actions/workflows/windows.yml) tests and builds every push to `main` and every pull request. Download **LootLedger-win-x64** from a successful run, or select **Run workflow** for a manual build.

To publish, push a tag such as `v1.0.0`. CI attaches the EXE, ZIP, and SHA-256 checksums to a GitHub release. Publishing a release in GitHub also triggers the build. No extra secrets are needed.

For a local build, run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build\Build.ps1`. To run from source, use **Start Loot Ledger.cmd**; source runs keep saves in `.loot-ledger/` beside the launcher.

## License

[MIT](LICENSE) · Independent project, not affiliated with or endorsed by Epic Games.
