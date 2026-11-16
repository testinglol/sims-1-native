# The Sims Native

Cross-platform C# (.NET 9) and SDL3 (`SDL_GPU`) client and simulation engine for *The Sims 1*.

No game assets are included. You need an existing installation of *The Sims 1* (such as *Complete Collection* or *Legacy Collection*) containing `GameData` and `UIGraphics`. Game files are read-only and never modified.

## Releases

Standalone archives include the .NET 9 runtime, SDL3, and compiled shaders:

- macOS (Apple Silicon, macOS 14+): extract `The-Sims-Native-macOS-arm64.zip` and open `The Sims Native.app` (Control-click -> Open on first run; ad-hoc signed).
- Windows (x64): extract `The-Sims-Native-Windows-x64.zip` and run `SimsSDL.exe`.
- Linux (x64): extract `The-Sims-Native-Linux-x64.zip` and run `./SimsSDL`.

On first launch, select your *The Sims 1* install directory (or pass `--game <dir>` / set `SIMS_GAME_DIR`).

## User Data

Saves, profiles, and settings are stored outside the game directory:

- macOS: `~/Library/Application Support/TheSimsNative`
- Windows: `%LOCALAPPDATA%\TheSimsNative`
- Linux: `$XDG_DATA_HOME/TheSimsNative` (or `~/.local/share/TheSimsNative`)

Layout inside the data directory:

- `Settings/install.json`: saved game path
- `runtime/profiles/`: player profiles, neighborhoods, lots, and rolling save backups (`Saves/backups`)
- `runtime/sessions/`: session logs (`simulation.log`)

## Controls

- Camera: right-drag to pan, scroll or `+`/`-` to zoom, `[`/`]` or `,`/`.` to rotate
- Simulation: `Space` or `P` to pause, `1`/`2`/`3` for speed, `Tab` to cycle Sims
- View: `Page Up`/`Page Down` for floors, `W` for wall cutaway mode, `Home`/`End` for walls and roofs
- Modes: `F1`–`F5` for Live/Buy/Build/Camera/Options, `S`/`L` to save/load, `Enter`/`Escape` for dialogs
- Editing: `R` rotates held objects, `Delete`/`Backspace` sells, `Ctrl` deletes in Build mode, `Shift` fills rooms or draws wall boxes

## Building

Requires the .NET 9 SDK and SDL3 (`brew install sdl3` on macOS, `SDL3_DIR` / `-SdlDir` on Windows, `SDL3_DIR` or `SDL3_LIB` on Linux).

```sh
# macOS dev app (Sims Native Preview.app) and launcher
./tools/build_native_app.command
SIMS_GAME_DIR="/path/to/The Sims 1" ./tools/play_sdl.command

# Package release archives into dist/
./tools/package_release.command 0.2.0
pwsh -File tools/package_windows.ps1 -Version 0.2.0 -SdlDir C:\path\to\SDL3
./tools/package_linux.sh 0.2.0

# Run tests (asset-dependent tests skip if runtime/game and SIMS_GAME_DIR are absent)
dotnet test native/SimsEngine.Tests/SimsEngine.Tests.csproj -c Release -p:EnableWindowsTargeting=true --nologo
```

## Layout

- `native/SimsEngine`: SimAntics VM, routing, archive/audio/IFF formats, lot state
- `native/SimsSimulation`: lot host, scene exporter, catalog/neighborhood services, headless server (`--serve`)
- `native/SimsSDL`: SDL3 client, GPU renderer, UI overlays, audio output, SPIR-V and Metal shaders
- `native/SimsPluginApi`: C# plugin interfaces
- `native/SimsEngine.Tests`: xUnit test suite
- `tools/`: build, shader, packaging, and bundle validation scripts
