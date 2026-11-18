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

- `logs/latest.log`: live diagnostic log for the current/most recent launch (plus rolling `sims-native-*.log` session logs)
- `Settings/install.json`: saved game path
- `Settings/mods.json`: enabled packs, load order, and disabled `Downloads` subfolders
- `Packs/`: user content packs
- `runtime/profiles/`: player profiles, neighborhoods, lots, and rolling save backups (`Saves/backups`)
- `runtime/sessions/`: active runtime session scratch data

## Troubleshooting & Logs

If the game crashes or gets stuck during startup, neighborhood loading, or lot transitions:

1. Press **`Ctrl+Shift+L`** (`Cmd+Shift+L` on macOS) at any time—even on the loading splash screen—to open the `logs` folder with `latest.log` selected, or open it directly:
   - Windows: `%LOCALAPPDATA%\TheSimsNative\logs\latest.log`
   - macOS: `~/Library/Application Support/TheSimsNative/logs/latest.log`
   - Linux: `~/.local/share/TheSimsNative/logs/latest.log`
2. Optional launch flags for troubleshooting on Windows:
   - `SimsSDL.exe --gpu-driver vulkan` or `SimsSDL.exe --gpu-driver direct3d12` to test an alternate GPU backend
   - `SimsSDL.exe --no-audio` to isolate audio device issues
   - `SimsSDL.exe --log C:\path\to\sims.log` to write logs to a custom location

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
./tools/package_release.command 0.4.0
pwsh -File tools/package_windows.ps1 -Version 0.4.0 -SdlDir C:\path\to\SDL3
./tools/package_linux.sh 0.4.0

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
