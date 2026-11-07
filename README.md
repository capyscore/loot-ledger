# Loot Ledger

Loot Ledger is a click-through Windows overlay for Fortnite Battle Royale. It shows the current match's chest count and an all-time count that persists across sessions.

## Features

- Automatic chest counting from English HUD XP notices using Windows OCR.
- Current and previous match totals, plus a saved all-time total with backup recovery.
- Match detection from the local Fortnite log, with battle bus and spectator filtering.
- Duplicate suppression across repeated frames, enhanced OCR, and optional audio matches.
- Optional chest-opening sound matching using application loopback audio and your own WAV sample.
- A configurable keyboard shortcut for manually counting missed or broken chests.
- A click-through overlay with four saved corner positions, automatic hiding when you switch apps, and a desktop preview with sample counts.
- A system tray menu for visibility, shortcuts, sound settings, tracker status, and shutdown.
- Local processing without an account, API key, or telemetry service.

## Requirements

- 64-bit Windows 10 or Windows 11 with Windows PowerShell 5.1 and the Windows desktop .NET Framework components used by WPF and Windows Forms.
- The English (United States) Windows OCR language pack.
- Fortnite Battle Royale or Zero Build with an English HUD in **Windowed Fullscreen** mode.
- A writable project folder for saving counts and preferences.
- Optional audio matching requires Windows build 20348 or newer. The launcher uses Windows PowerShell; PowerShell 7 is not supported by the Windows Runtime integration used here.

No separate build, package installation, or administrator access is normally required. Keep all source files beside the launcher; it compiles the C# helpers when starting.

## Start tracking

1. Double-click **Start Loot Ledger.cmd**. Look for the gold chest in the Windows system tray. It may be under the hidden icons arrow.
2. In Fortnite, set **Window Mode** to **Windowed Fullscreen** and use the English HUD. Launch a Battle Royale or Zero Build match.
3. Open chests normally. The overlay counts recognized chest XP notices and resets the match count when Fortnite logs the next match. Alt-tabbing hides the overlay until Fortnite is in front again.

Right-click the tray icon for a preview, tracker status, the in-game visibility toggle, and exit. The preview uses sample numbers. Live totals are saved as you play in `.loot-ledger/counts.json` next to the launcher. Use **Exit Loot Ledger** in the tray for a normal shutdown.

Choose **Position** in the tray menu to move the overlay to the top left, bottom left, top right, or bottom right. Loot Ledger remembers the corner, and **Show preview** lets you check it on the desktop.

For a broken chest that produced no automatic count, turn on **Enable chest hotkey** in the tray and press the shortcut once per chest. The default is **Caps Lock**. Click **Chest hotkey** to open the capture dialog, then press a key or hold a combination together (for example **PageDown**, **F8**, **Ctrl+Shift+K**, or **J+K**). Release the keys and click **Save** to apply the shortcut and close the dialog, or click **Cancel** to keep the current shortcut. The saved shortcut updates immediately beside **Chest hotkey** in the tray menu and in the tray icon's tooltip. It counts once per press while Fortnite is active during a match. Loot Ledger saves your shortcut and whether it is enabled.

## Optional sound detection

1. Export a clean, tightly trimmed clip of one chest **opening** as a 16-bit PCM WAV, 0.25 to 2 seconds long. Avoid the ambient hum before opening, voices, and music.
2. Right-click the tray icon, choose **Load chest sound WAV...**, and select the file.
3. Enable **Count unconfirmed sound matches**. The badge switches to **SFX+** when capture is running in a match.

Sound mode matches the sample against audio from the Fortnite process. It requires 64-bit Windows 11 or another Windows build that supports application loopback (build 20348 or newer). HUD and audio detections that line up are counted together. The sample works best with the same chest type and similar game audio settings. Nearby players can produce matching sounds, so sound-only matches are opt-in. [Microsoft application loopback reference](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)

## What gets counted

- Each distinct readable chest XP notice can count, even when several are visible together. An explicit quantity such as `CHEST x2` counts as two. A merged notice with no quantity, or identical chimes that overlap, can undercount a burst.
- The battle bus phase is excluded. An early jump enables tracking when the local deploy/skydive prompt is recognized; otherwise tracking starts at the logged SafeZones phase.
- Recognition combines the original HUD with an enlarged, contrast-enhanced version for dark scenes. Both passes share notice tracking, and brief OCR gaps or partial fading text do not count the same chest again.
- Screen recognition depends on the notice being readable. A missed or suppressed notice, an unrecognized chest label, or a chest broken without a search XP notice can be missed.
- Tracking is designed for the English Battle Royale HUD and its local match log. Other modes may use different HUD text or match markers. A Windows English (United States) OCR language pack is needed.

The app reads a region of the visible game window and the local Fortnite log. Screenshots are processed in memory. The overlay runs separately from the game.

## Privacy and local data

The game log path is resolved through `%LOCALAPPDATA%`; the source does not contain a personal Windows username or an absolute user directory. The app does not upload logs, screenshots, audio, or counts, and does not request game credentials.

Counts, a local match identifier derived from the log header and position, preferences, backups, and any imported WAV sample stay in `.loot-ledger/` beside the launcher. This directory is ignored by Git, along with common secret files, logs, recordings, and compiled output. Keep it out of source archives and support attachments. Preview mode uses sample values rather than saved totals.

## Source layout

| File | Purpose |
| --- | --- |
| `Start Loot Ledger.cmd` | Starts the app with Windows PowerShell in STA mode. |
| `LootLedger.ps1` | Coordinates OCR, detection, persistence, overlay updates, and tray actions. |
| `Overlay.xaml` | Defines the WPF overlay appearance. |
| `Tracking.cs` | Reads match phases, captures and enhances HUD images, and combines detections. |
| `ChestSound.cs` | Loads WAV templates and matches process-loopback audio. |
| `TrayUi.cs` | Implements shortcut capture and tray menu rendering. |
| `Tests/` | Tracking, OCR, and shortcut regression checks with synthetic inputs. |

## Development checks

Run these commands from the project folder in Windows PowerShell 5.1. They do not require a running game:

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\Tests\Test-Tracking.ps1
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\Tests\Test-TrayUi.ps1
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\Tests\Test-HudOcr.ps1
```

The OCR check needs the English (US) OCR language pack. The shortcut checks instantiate Windows Forms controls and need a Windows desktop session.

## License

Released under the [MIT License](LICENSE). Fortnite is a trademark of Epic Games. This is an independent project and is not affiliated with or endorsed by Epic Games. No Fortnite audio sample is included.
