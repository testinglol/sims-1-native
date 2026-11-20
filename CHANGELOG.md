# Changelog

All notable changes to **Plumbob Engine** are documented in this file.

## [0.5.1] - 2026-11-20

### Roof Rendering, Snowy Roofs, Seasons & Branding
- **Fixed Snowy Roofs in Winter**: Updated `LotRenderer.EncodeSimMask` to exclude roof meshes (`Roof != null`) from `simMap` so rooftops accumulate winter snow alongside the terrain, trees, and hedges.
- **Fixed Roof Disappearance on Animation Ticks**: Fixed `LotRenderer.ApplyAnimation` resetting `_meshSlices` from `_staticMeshSlices` so roof meshes appended after dynamic avatar meshes never lose their vertex buffer slices on the first animation tick.
- **Fixed Walls Poking Through Rooftops**: Added a forward roof depth bias in `SceneExporter` so wall-top corners beneath sloped eaves never z-fight or poke through roofs.
- **Plumbob Engine Branding, Save Data & Logs**: Updated user-facing app title, crash dialog, default save data root (`PlumbobEngine`), log filenames (`plumbob-engine-*.log`), and standalone release bundles (`Plumbob Engine.app` / `Plumbob-Engine-{macOS-arm64,Windows-x64,Linux-x64}.zip`).
- **Asset-Clean Seasons Snowman & Cross-Platform Pack Fixes**: Updated `PluginWorld` and `LocalSeasons` so `Build Snowman Here` places the player's own Vacation `Snowman.iff` directly (zero bundled `.iff` assets), updated `PluginHost.ExportEnvironment` to stage `.dxil` pack shaders for Windows Direct3D 12, and fixed bundled plugin auto-trust canonical path matching on Windows.

---

## [0.5.0] - 2026-11-20

### Preinstalled Official Packs (Enabled by Default)
- **Enhanced Graphics (`local.lighting`) & Seasons (`local.seasons`) Bundled Out of the Box**: Both official packs now ship preinstalled inside the standalone macOS (`Contents/Resources/Packs`), Windows (`Packs/`), and Linux (`Packs/`) releases, enabled by default on first launch and automatically trusted without prompting. Players can toggle or reorder them at any time in the in-game **Mods** menu (`Settings/mods.json`).
- **Full Cross-Platform Pack Shaders (Metal, Vulkan, Direct3D 12)**: Added HLSL SM 6.0 / signed DXIL compilation and stage-linkage validation for bundled pack shaders (including fixing `terrain.frag` `OSG1 -> ISG1` linkage on Direct3D 12 and adding `.dxil` support to `ContentPack`), so Enhanced Graphics and Seasons render natively on Windows Direct3D 12 as well as macOS Metal and Linux Vulkan.

### Connected Neighborhood
- **Multi-Lot & Neighbor Visit Architecture**: Added `NeighborhoodManager`, `NeighborLotController`, `GatewayRouting`, `NeighborhoodStreetGraph`, `NeighborhoodStreetLayout`, and `MasterRoadNetwork` alongside client/renderer integration (`LiveController.ConnectedNeighborhood`, `LotRenderer.ConnectedNeighborhood`) for visiting neighbor lots, split-household multi-lot coordination, and adjacent-lot isometric rendering.

### Audio & HIT VM Parity
- **HIT Bytecode VM**: Implemented the original *The Sims* HIT audio scripting VM (`HitVm`, `HitVmTypes`) with expanded `SoundBank`, `UtkAudio`, and `SDLAudioPlayer` parity.

### Vector UI, Cheat Console & Graphics Polish
- **Complete Vector Control Panel & Fitted Chrome**: Added pure-SVG vector icons across all Build tools (walls, wallpaper, floors, doors, windows, stairs, fireplace, roof, terrain, water, trees, hand, undo/redo) and Buy categories (function, room, and pets), plus fitted vector panel chrome (`SimsVectorUiKit.FittedChrome`, `SimsVectorUiKit.FittedPanels`) and improved Options, Neighborhood, Gameplay, Buy, Build, Camera, and Household Creation overlays.
- **In-Game Cheat Console (`OriginalCheatConsoleOverlay`)**: Added the classic cheat console overlay for entering cheats directly in-game.
- **Rendering & Thumbnail Refinements**: Updated 3D avatar lighting and sprite depth shaders (`avatar.vert`, `avatar.frag`, `spriteDepth.frag`), improved catalog thumbnail anti-aliasing, and refined procedural roof bounds and scene picking.

### Simulation, Animation & Save Parity
- **Overhead Feedback & Animation Transitions**: Added `World.OverheadFeedback` headline/balloon handling, smoother Sim animation transitions and pose blending (`Pose`, `AvatarAnimations`, `AvatarSceneGeometry`), and vehicle drive-away regression fixes.
- **SimAntics & Build Tool Fixes**: Improved `AnimateSim`, `GenericSimsCall`, `InteractionTestEvaluator`, `Relationship`, `RouteExecutor`, `MakeNewCharacter`, `ReachOps`, `SpecialEffect`, `Tutorial`, `WardrobeSuitService`, `WallTool`, `PoolTool`, `SavedLotObjectLoader`, `SavedLotSerialization`, and `SaveCompatibility`.

---

## [0.4.1] - 2026-11-18

### Windows Direct3D 12 Crash Fix (`0x80070057` / `E_INVALIDARG`)
- **Fixed D3D12 Graphics Pipeline Creation Crash**: Fixed inter-stage HLSL/DXIL register linkage mismatches (`OSG1` -> `ISG1`) in `sprite.frag`, `ghost.frag`, `censor.frag`, and `terrain.frag` where skipped fragment `layout(location)` inputs shifted packed DXC `TEXCOORD` / `SV_Position` register indices relative to `sprite.vert` and `terrain.vert`, causing `SDL_CreateGPUGraphicsPipeline` (`CreateGraphicsPipelineState`) to fail with `0x80070057` (`E_INVALIDARG`) on Direct3D 12 at startup and lot entry.
- **Automated HLSL/DXIL Linkage & Live Windows D3D12 CI Verification**: Added `tools/build_sdl_hlsl_shaders.py`, Windows SDK `dxc.exe` DXIL compilation during CI/packaging, binary `OSG1`/`ISG1` signature validation, and an automated Windows Direct3D 12 `GPUContext.Pipeline` test suite across all core shader programs, depth modes, and MSAA sample counts.
- **Crash Dialog & Watchdog Polish**: Prevented false stall-watchdog log entries while the native crash report dialog is waiting on user input, fixed decimal second formatting in watchdog alerts, and shortened the crash dialog button label (`Open Logs`) so it never clips at 135%+ Windows DPI scaling.

---

## [0.4.0] - 2026-11-18

### Diagnostics, Logging & Windows Hang Fixes
- **Automatic Session & Rolling Logs (`logs/latest.log`)**: Every launch now writes a timestamped session log (`sims-native-YYYYMMDD-HHmmss-PID.log`, retaining the 10 most recent sessions) and mirrors live output to `logs/latest.log` inside the user data folder (`%LOCALAPPDATA%\TheSimsNative\logs\latest.log` on Windows, `~/Library/Application Support/TheSimsNative/logs/latest.log` on macOS, `~/.local/share/TheSimsNative/logs/latest.log` on Linux), with automatic `%TEMP%\TheSimsNative\logs` fallback if the data root is unavailable.
- **Startup, Bridge & GPU Stage Tracing + Stall Watchdog**: Added end-to-end `[STAGE]` timing breadcrumbs across SDL video/audio driver initialization, game folder resolution & picker validation, shader/GPU device creation, content pack & `Downloads` mounting, `UserData` neighborhood import, warm simulation daemon startup, IPC bridge lifecycle commands (`neighborhood`, `openLot`, `save`, `load`), and first-frame presentation, paired with a background stall watchdog that automatically logs `[WATCHDOG] STALL DETECTED` / `MAIN THREAD UNRESPONSIVE` entries to `latest.log` if startup, lot/neighborhood loading, or rendering hangs.
- **Instant Log Reveal Shortcut & Crash Dialog**: Press **`Ctrl+Shift+L`** on Windows/Linux (**`Cmd+Shift+L`** on macOS) at any point—including on the loading splash screen—to highlight `latest.log` in File Explorer / Finder. Unhandled crashes and fatal initial boot errors now present an **Open Logs Folder** action instead of stranding the window on an empty lot HUD.
- **CLI Overrides (`--log`, `--gpu-driver`)**: Added `--log <path>` (or `SIMS_LOG_FILE`) to write logs to a custom file and `--gpu-driver <direct3d12|vulkan|metal>` (or `SIMS_GPU_DRIVER`) to force a specific `SDL_GPU` backend when troubleshooting graphics drivers on Windows.
- **Windows Minimized / Zero-Swapchain Freeze Fix**: Fixed a Win32 event-loop freeze where minimizing or zero-sizing the window (`0x0` swapchain) skipped `SDL_PollEvent` and spun at 100% CPU (`Not Responding`).
- **Windows Read-Only ISO / Disc Install Import Fix**: Cleared `FileAttributes.ReadOnly` when importing and merging `UserData` and `Template*` neighborhood files into the player profile so read-only ISO/CD installations no longer fail with `UnauthorizedAccessException` on Windows.
- **Nested Game Folder Selection**: Selecting an outer wrapper folder in the first-launch folder picker now automatically resolves a single valid *The Sims 1* child directory containing `GameData` and `UIGraphics`.

### Vector UI & 4K Extended Neighborhood Backdrops
- **Pure-SVG Vector UI Kit**: Embedded crisp, resolution-independent vector icons and dynamic banners (`SvgIconRenderer`, `VectorBannerRenderer`, `SimsVectorUiKit`) for the live control panel, speed controls, neighborhood navigation bars, career icons, and window chrome—eliminating external raster HD UI pack folders across 1080p, 2K, and 4K displays.
- **4K Extended Widescreen Neighborhood Maps**: Added seamless 4K extended backdrops (`ExtendedNeighborhoodBackdrop`) for the Unleashed coastal base neighborhood, Downtown, Vacation Island, Studio Town, and Magic Town, with smooth zoom interpolation and continuous arrow-key panning.

### In-Game Mods & Custom Content Manager
- **Neighborhood Mods Menu (`ModsManager`)**: Added a **Mods** button to the neighborhood banner for discovering, enabling/disabling, and reordering structured content packs (`Packs/`) and classic `Downloads/` subfolders, while automatically filtering built-in official Maxis SimDownload `.far` bonus sets.
- **Conflict Validation & Safe Mode Recovery**: Displays soft file-override badges, hard GUID/resource/dependency conflicts, and plugin trust status, persisting load order in `Settings/mods.json` with automatic Safe Mode fallback if a saved mod breaks on startup.

### Gameplay, Audio & Plugin API v4
- **Plugin API v4**: Added ground terrain pie-menu hooks (`GetGroundMenuEntries` / `OnGroundMenuEntry`), pack-owned lot object placement/removal (`TryPlaceOwnedObject` / `TryRemoveOwnedObject`), transient Sim outdoor suit overrides (`SetSimSuitOverride`, `WearingOutdoorSuit`), and looping environmental ambient sound beds (`PluginAmbientSound`).
- **Seasonal Outdoor Outfits, Hand-Prop Meshes & Shelter Map**: Added `SeasonalOutfitService` for automatic outdoor winterwear transitions, custom 3D OBJ/PNG Sim hand-prop bone attachments (`hand-prop-attachments.txt`), subtile roof/floor shelter overhead maps in `SceneExporter`, and seasonal/weather shader uniforms (`snowfall`, `frost`, `hail`, `thunder`, `springBloom`, `fireflies`, `grassQuality`) in `LotRenderer`.

---

## [0.3.0] - 2026-11-17

- Added HiDPI SharpText TrueType typography and automatic 2K/4K UI scaling (`Ctrl`/`Cmd` + `+`/`-`/`0`).
- Added Direct3D 12 (`.dxil`) compiled shaders alongside SPIR-V (`.spv`) and Metal (`.msl`) shaders for Windows `SDL_GPU` compatibility.
- Added *The Sims Online* `0x8003` BHAV primitive support and custom-content object crash containment when loading saved lots.

## [0.2.0] - 2026-11-16

- Unified standalone cross-platform release packaging for macOS (`arm64`), Windows (`x64`), and Linux (`x64`) with in-process .NET 9 simulation engine and SDL3 GPU renderer.

## [0.1.0] - 2026-11-16

- Initial public preview release of **The Sims Native**.
