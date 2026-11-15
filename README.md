# The Sims Native

An open-source, cross-platform reconstruction of *The Sims 1* written entirely in .NET 9 (C#) with direct SDL3 / `SDL_GPU` rendering (`SimsSDL`) running the simulation engine (`SimsEngine` + `SimsSimulation`) in-process.

> **Work in progress:** Household simulation, Live/Buy/Build modes, neighborhood and family management, and sub-neighborhood travel are under active development. Many original behaviors and UI panels are still incomplete.
>
> **No game assets included:** This repository and its prebuilt releases contain zero Electronic Arts files. You must provide your own legal installation of *The Sims 1* (for example, *The Sims Complete Collection* or *Legacy Collection*) containing `GameData` and `UIGraphics`.

---

## Prebuilt Releases (macOS, Windows & Linux)

Prebuilt standalone archives are built for Apple Silicon macOS, 64-bit Windows, and 64-bit Linux. Each bundle includes the self-contained .NET 9 runtime, SDL3, and compiled GPU shaders — no separate .NET or SDL installation is needed to play.

### macOS (Apple Silicon, macOS 14+)

1. Download **`The-Sims-Native-macOS-arm64.zip`** from the Releases page and extract **`The Sims Native.app`**.
2. On first launch, **Control-click -> Open** (the bundle is ad-hoc signed, not Apple-notarized).
3. When prompted, select your installed *The Sims 1* folder (containing `GameData` and `UIGraphics`). Your original game files remain untouched and read-only.

### Windows (64-bit Windows 10/11)

1. Download **`The-Sims-Native-Windows-x64.zip`** from the Releases page and extract the **`The Sims Native`** folder.
2. Run **`SimsSDL.exe`**.
3. When prompted, select your installed *The Sims 1* folder (containing `GameData` and `UIGraphics`). Your original game files remain untouched and read-only.

### Linux (x86_64)

1. Download **`The-Sims-Native-Linux-x64.zip`** from the Releases page and extract the **`The Sims Native`** folder.
2. Run **`./SimsSDL`**.
3. When prompted, select your installed *The Sims 1* folder (containing `GameData` and `UIGraphics`). Your original game files remain untouched and read-only.

---

## Where Saves, Profiles & Settings Live

Standalone release builds store player data outside both the app bundle and your original *The Sims 1* folder:

| Platform | Data Root |
| --- | --- |
| **macOS** | `~/Library/Application Support/TheSimsNative` |
| **Windows** | `%LOCALAPPDATA%\TheSimsNative` |
| **Linux** | `$XDG_DATA_HOME/TheSimsNative` (fallback `~/.local/share/TheSimsNative`) |

Inside the data root:

- `Settings/install.json` — remembered *The Sims 1* folder path (can also be overridden via `SIMS_GAME_DIR` or `--game <dir>`)
- `runtime/profiles/` — player profiles and saved neighborhoods/lots
- `runtime/sessions/` — live session directories and `simulation.log`

Automatic save backups are stored under `<profile>/Saves/backups` (newest five per slot). To uninstall, delete `The Sims Native.app` (macOS) or the extracted `The Sims Native` folder (Windows), and optionally remove the data root directory above; your original *The Sims 1* installation is never modified.

---

## Basic Controls

- **Pan / Zoom:** right-drag to pan, scroll wheel or `+`/`-` to zoom
- **Rotate:** `[`/`]` or `,`/`.` (also rotates a held object)
- **Speed & Pause:** `P` or `Space` to pause/resume; `1`, `2`, `3` for speed
- **Selection & Floors/Walls:** `Tab` cycles Sims; `Page Up`/`Page Down` switches stories; `W` cycles wall cutaway mode; `Home`/`End` steps wall/roof visibility
- **Modes & Dialogs:** `F1`–`F5` switch Live/Buy/Build/Camera/Options modes; `S`/`L` save/load; `Enter`/`Escape` accept/cancel dialogs
- **Build & Buy:** `R` rotates a held object; `Delete`/`Backspace` sells a held object; hold `Ctrl` in Build mode to delete or `Shift` for room fill / wall boxes

---

## Building from Source

### Prerequisites

- **macOS:** .NET 9 SDK, SDL3 (`brew install sdl3`)
- **Windows:** .NET 9 SDK and an SDL3 runtime/VC package containing `SDL3.dll` (set `SDL3_DIR` or pass `-SdlDir`)
- **Linux:** .NET 9 SDK and SDL3 shared library (`libSDL3.so.0`, set `SDL3_DIR` or `SDL3_LIB`)

### Build & Package Commands

On **macOS**:

```sh
# Build local dev app (Sims Native Preview.app)
./tools/build_native_app.command

# Build standalone arm64 release bundle & zip in dist/
./tools/package_release.command 0.1.0

# Launch dev client against a local game directory (SIMS_GAME_DIR or runtime/game)
SIMS_GAME_DIR="/path/to/The Sims 1" ./tools/play_sdl.command
```

On **Windows (PowerShell)**:

```powershell
# Build standalone win-x64 release folder & zip in dist/
pwsh -File tools/package_windows.ps1 -Version 0.1.0 -SdlDir C:\path\to\SDL3-3.x.y
```

On **Linux**:

```sh
# Build standalone linux-x64 release folder & zip in dist/
./tools/package_linux.sh 0.1.0
```

### Running Tests

```sh
# .NET 9 engine, simulation, ABI, and client tests (tests requiring original game assets skip cleanly if runtime/game or SIMS_GAME_DIR is absent)
dotnet test native/SimsEngine.Tests/SimsEngine.Tests.csproj -c Release -p:EnableWindowsTargeting=true --nologo
```

---

## Repository Layout

- [`native/SimsSDL`](native/SimsSDL): .NET 9 SDL3 / `SDL_GPU` client, original UI overlays, audio player, core parsers/layouts, and precompiled SPIR-V / Metal shaders
- [`native/SimsEngine`](native/SimsEngine): .NET 9 core simulation engine, SimAntics VM, routing, audio/archive codecs, and lot persistence
- [`native/SimsPluginApi`](native/SimsPluginApi): plugin interfaces and world hooks
- [`native/SimsSimulation`](native/SimsSimulation): simulation host & headless server (`--serve`), also hosted in-process by `SimsSDL`
- [`native/SimsEngine.Tests`](native/SimsEngine.Tests): xUnit test suite
- [`tools/`](tools): macOS & Windows packaging scripts (`package_release.command`, `package_windows.ps1`, `build_native_app.command`) and bundle checks (`tools/checks/check_release_bundle.py`)
- [`dist/`](dist): application icon assets (`sims1-icon.icns`, `sims1-icon.png`)
