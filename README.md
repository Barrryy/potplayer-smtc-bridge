# PotPlayer SMTC Bridge

Provide complete media metadata to the Windows **SMTC** (System Media Transport Controls) session published by PotPlayer.

**English** · [中文](README.zh-CN.md)

---

## Quick start

> **Requirements** — Windows 10/11 x64 · PotPlayer x64 · administrator rights for the install step.

### 1 · Build

Skip this step if you downloaded the release archive.

```powershell
$env:Path = "D:\mingw64\bin;" + $env:Path
.\build.ps1
```

### 2 · Install

Run the configuration GUI, select **Install injection**, then approve the elevation prompt.

```
build\PotPlayerSmtcBridge.exe
```

### 3 · Use

Launch PotPlayer and play any file. The Windows media panel — and every other SMTC consumer — now shows the full title, artist, album, track number, genre and artwork.

### Uninstall

Select **Uninstall injection** in the GUI, or delete the registry key:

```powershell
Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PotPlayerMini64.exe' -Recurse -Force
```

## Overview

PotPlayer publishes an SMTC session, but several media properties remain empty or unusable. The most significant gaps are:

| Property | PotPlayer behaviour |
| :--- | :--- |
| `Title` | Present, but set to the file name including its extension |
| `Subtitle`, `Artist`, `AlbumArtist`, `AlbumTitle`, `TrackNumber`, `AlbumTrackCount`, `Genres` | Empty |
| `Thumbnail` | Present, but an uncompressed BMP (typically around 1.5 MB) |
| `PlaybackType` | Present, but reported as `Music` for video as well |

The remaining properties (playback status, playback rate, repeat and shuffle state, timeline, available controls) are populated correctly and are not modified by this project.

Restoring the missing properties allows every SMTC consumer — the Windows media flyout, Discord, Last.fm clients and lyric tools — to display the correct title, artist, album, track number and artwork.

## Why process injection is required

Windows SMTC separates writing from reading:

- The write interface `Windows.Media.SystemMediaTransportControls` is an in-process object, reachable only from the process that created it.
- The read interface `GlobalSystemMediaTransportControlsSessionManager` exposes read-only properties only.

No public API allows one process to modify the SMTC session of another. Consequently, the metadata of the PotPlayer session can only be completed by code running inside the PotPlayer process.

PotPlayer's SMTC implementation resides in `MediaDB64.dll` (internal name `StreetPlayer`). This project hooks the WinRT activation calls and file-open calls made by that module, and writes the media properties through the display updater before the host submits them.

## How installation works (IFEO startup injection)

> The earlier "import table patch" approach (v0.6) is discontinued. `PotPlayerMini64.exe` is protected with Themida (WinLicense) and verifies both its file length and its contents; `PotPlayer64.dll` and `MediaDB64.dll` carry Kakao code-signing certificates and are validated with `WinVerifyTrust`. Modifying any PotPlayer file causes it to refuse to start. Evidence is recorded in [docs/06-ifeo-install.md](docs/06-ifeo-install.md).

Installation writes a single registry key:

```
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PotPlayerMini64.exe
    Debugger = "<install folder>\PotPlayerSmtcInjector.exe"
```

On every PotPlayer launch, Windows starts the injector first. The injector:

1. creates the PotPlayer process with `DEBUG_ONLY_THIS_PROCESS`, which prevents IFEO from intercepting the child process a second time (a plain `CreateProcess` results in unbounded recursion);
2. detaches the debugger with `DebugActiveProcessStop` while the target is still suspended by the kernel, so `PEB.BeingDebugged` is already zero before the first instruction runs and Themida does not observe a debugger;
3. waits until the target loader is initialised (`kernel32.dll` mapped), then injects `PotPlayerSmtcHook.dll` with `CreateRemoteThread(LoadLibraryW)`;
4. terminates immediately.

The resulting configuration uses no resident process, no Windows service and no startup entry, and does not modify any PotPlayer file. Uninstallation removes the registry key, and a PotPlayer update does not require reinstalling.

## Requirements

- Windows 10 or Windows 11, x64
- PotPlayer x64 (the injector targets `PotPlayerMini64.exe`)
- Administrator rights for installation (the IFEO key resides in `HKLM`)
- To build from source: MinGW-w64 g++ (x86_64) and the .NET 10 SDK

## Build

```powershell
$env:Path = "D:\mingw64\bin;" + $env:Path
.\build.ps1            # injector, hook module, startup injector and GUI
.\build.ps1 -Tests     # additionally builds the test target
```

Build output is written to `build/`:

| File | Description |
| :--- | :--- |
| `PotPlayerSmtcInjector.exe` | Startup injector launched by the IFEO rule |
| `PotPlayerSmtcHook.dll` | Hook module loaded into the PotPlayer process |
| `PotPlayerSmtcBridge.exe` | Configuration GUI |
| `SmtcLoader.exe` | Standalone injector for manual testing |

## Installation details

1. Run `build\PotPlayerSmtcBridge.exe`.
2. Complete the first-run wizard: locate the PotPlayer executable and review the metadata rules.
3. In the main window, select **Install injection** and confirm the elevation prompt.

After installation, launch PotPlayer normally; no further action is required.

To uninstall, select **Uninstall injection** in the main window, or run:

```powershell
Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PotPlayerMini64.exe' -Recurse -Force
```

## Files and locations

| Path | Contents |
| :--- | :--- |
| `%LOCALAPPDATA%\PotPlayerSmtcBridge\` | Installed injector, hook module, rule file and file-hook marker |
| Program folder | Source tree, build output and the GUI |
| `%TEMP%\potplayer-smtc-bridge\` | Runtime logs (`injector.log`, `hook-<pid>-<timestamp>.log`) |

**Do not delete or move the files inside the install folder manually.** The IFEO rule points to the injector in that folder; if the file is missing, Windows cannot start PotPlayer. If this occurs, delete the registry key listed above to restore normal operation.

## Metadata rules

When files do not follow a consistent naming scheme, metadata is read from the file tags through the Windows property system. The resolution order is:

| Field | Source order |
| :--- | :--- |
| `Title` | tag `Title` → segment after `" - "` in the file name → whole file name |
| `Artist` | tag `Artist` → segment before `" - "` in the file name → empty |
| `AlbumTitle` | tag `Album` → empty |
| `AlbumArtist` | tag `AlbumArtist` → tag `Artist` → segment before `" - "` → empty |
| `TrackNumber`, `AlbumTrackCount`, `Genres` | tags |

The file-name pattern uses the placeholders `%title%`, `%artist%`, `%album%`, `%albumArtist%` and `%track%`. All other characters are matched literally; `%%` denotes a literal percent sign. Rules are stored in `PotPlayerSmtcHook.ini` next to the hook module and are re-read every two seconds, so no player restart is required.

Properties are cleared when the next track does not provide a value, so metadata never carries over from the previous track.

## Command line

| Command | Description |
| :--- | :--- |
| `PotPlayerSmtcBridge.exe` | Opens the main window |
| `PotPlayerSmtcBridge.exe --silent` | Runs without a window until signalled |
| `PotPlayerSmtcBridge.exe --install-ifeo <player.exe>` | Installs the IFEO rule (elevated) |
| `PotPlayerSmtcBridge.exe --uninstall-ifeo <player.exe>` | Removes the IFEO rule (elevated) |
| `PotPlayerSmtcBridge.exe --restore <player.exe>` | Restores a player executable modified by v0.6 (elevated) |
| `SmtcLoader.exe --name <process>` | Injects the hook module into a running process |
| `SmtcLoader.exe --launch <player.exe>` | Starts the player suspended and injects during startup |
| `SmtcLoader.exe --watch` | Waits for the player to start and injects |

## Verification with the built-in test target

The test target imitates `PotPlayerMini64.exe`, opens a few placeholder media files and waits for injection. It is safe to run and is recommended for validating a build before touching the player:

```powershell
.\build.ps1 -Tests
$p = Start-Process .\build\test\PotPlayerMini64.exe -PassThru
.\build\SmtcLoader.exe --name PotPlayerMini64.exe --dll .\build\test\PotPlayerSmtcHook.dll
python .\tools\summarize-log.py
```

A successful run records the module list, the window list, the patched import entries and one `[open]` line per opened file.

## Troubleshooting

| Symptom | Resolution |
| :--- | :--- |
| PotPlayer does not start | The IFEO rule points to a missing injector. Delete the registry key listed above. |
| No metadata in Windows | Confirm the injector log contains `inject ok`, then verify that all four files are present in the install folder. |
| Fields retained from the previous track | Update to the current build; earlier releases did not clear empty properties. |
| Metadata is taken from the file name | The file has no tags, or the rule order has been changed in the main window. |
| Injection fails under a hardened configuration | Process mitigation policies (ACG/CIG) can block injection. |

Logs are written to `%TEMP%\potplayer-smtc-bridge\`. Crash dumps produced by the host are written to `%APPDATA%\DAUM\PotPlayer\Log\`.

## Project layout

```
.
├─ build.ps1                      # build script (MinGW + .NET)
├─ docs/                          # design notes and field mapping
│  ├─ 01-feasibility.md           # feasibility analysis and field gaps
│  ├─ 02-architecture.md          # architecture and timing design
│  ├─ 03-smtc-field-map.md        # SMTC field inventory and data sources
│  ├─ 04-injection-risks.md       # risk register and mitigations
│  ├─ 05-smtc-abi.md              # interface layout and vtable indices
│  └─ 06-ifeo-install.md          # IFEO installation mechanism and evidence
├─ src/
│  ├─ SmtcBridge.App/             # configuration GUI (.NET)
│  ├─ SmtcBridge.Injector/        # startup injector
│  ├─ PotPlayerSmtcHook/          # hook module
│  └─ SmtcLoader/                 # standalone injector
├─ tests/
│  ├─ SmtcHookTestTarget/         # injection test target
│  └─ IfeoProbe/                  # IFEO and debugger-detach probes
└─ tools/
   ├─ uninstall-smtc-bridge.ps1   # uninstall script
   ├─ summarize-log.py            # log summariser
   └─ winmd-dump/                 # interface dump from Windows.Media.winmd
```

## Known risks

Injection carries inherent costs; the complete register is maintained in [docs/04-injection-risks.md](docs/04-injection-risks.md).

- **Stability**: the hook module runs in the PotPlayer process; an unhandled fault can terminate the player.
- **Antivirus** false positives: remote thread injection is monitored by security software; an exclusion may be required.
- **Process mitigation policies**: ACG/CIG block injection outright.
- **Version compatibility**: PotPlayer updates can change internal behaviour. The project hooks COM vtables and export tables rather than relying on hard-coded offsets.

## Disclaimer

This project modifies the runtime behaviour of PotPlayer through process injection. It is an unofficial tool for personal use, is not affiliated with and not supported by Kakao Corp.

Use it only on devices you own. Do not distribute prebuilt injectors. Ensure the risks above are understood before installation.

## License

[MIT](LICENSE)
