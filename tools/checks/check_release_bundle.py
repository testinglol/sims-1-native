#!/usr/bin/env python3
"""Validate standalone macOS and Windows release bundles for The Sims Native."""

from __future__ import annotations

import argparse
import hashlib
import subprocess
import sys
from pathlib import Path

GAME_ASSET_EXTENSIONS = {
    ".bmf",
    ".cfp",
    ".cmx",
    ".fam",
    ".far",
    ".iff",
    ".pub",
    ".skn",
    ".spf",
    ".utk",
    ".xa",
}
IGNORED_HASH_NAMES = {".ds_store", "thumbs.db", "desktop.ini"}
FORBIDDEN_BUNDLE_EXTENSIONS = {".log", ".pdb", ".dsym"}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def collect_game_hashes(game_dirs: list[Path]) -> dict[str, str]:
    hashes: dict[str, str] = {}
    for game_dir in game_dirs:
        if not game_dir.is_dir():
            continue
        for item in game_dir.rglob("*"):
            if not item.is_file():
                continue
            if item.name.lower() in IGNORED_HASH_NAMES or item.stat().st_size == 0:
                continue
            hashes.setdefault(sha256_file(item), str(item))
    return hashes


def detect_platform(bundle: Path, platform_arg: str) -> str:
    if platform_arg != "auto":
        return platform_arg
    if bundle.suffix == ".app" or (bundle / "Contents" / "MacOS").is_dir():
        return "macos"
    if (bundle / "SimsSDL.exe").is_file() or (bundle / "SDL3.dll").is_file():
        return "windows"
    if (bundle / "SimsSDL").is_file() or (bundle / "libSDL3.so.0").is_file():
        return "linux"
    raise SystemExit(f"Could not detect bundle platform for: {bundle}")


def require_file(path: Path, label: str) -> None:
    if not path.is_file():
        raise SystemExit(f"Missing {label}: {path}")


def require_non_empty_shaders(path: Path, label: str) -> None:
    if not path.is_dir():
        raise SystemExit(f"Missing {label}: {path}")
    has_compiled = any(
        item.is_file() and item.suffix in {".msl", ".spv"} for item in path.iterdir()
    )
    if not has_compiled:
        raise SystemExit(f"Missing compiled shaders (.msl/.spv) in {label}: {path}")


def check_common(bundle: Path, game_dirs: list[Path]) -> None:
    if not bundle.is_dir():
        raise SystemExit(f"Release bundle directory not found: {bundle}")

    game_hashes = collect_game_hashes(game_dirs)
    for item in bundle.rglob("*"):
        if not item.is_file():
            continue
        name = item.name
        if item.suffix.lower() in GAME_ASSET_EXTENSIONS:
            raise SystemExit(f"Original game asset extension leaked into bundle: {item}")
        if item.suffix.lower() in FORBIDDEN_BUNDLE_EXTENSIONS:
            raise SystemExit(f"Forbidden debug/log artifact leaked into bundle: {item}")
        if game_hashes and name.lower() not in IGNORED_HASH_NAMES and item.stat().st_size > 0:
            match = game_hashes.get(sha256_file(item))
            if match is not None:
                raise SystemExit(f"Original game file leaked into bundle: {item} (matches {match})")


def check_otool_dependencies(binary: Path) -> None:
    result = subprocess.run(
        ["otool", "-L", str(binary)],
        check=True,
        capture_output=True,
        text=True,
    )
    lines = [line.strip().split(" (", 1)[0] for line in result.stdout.splitlines()[1:] if line.strip()]
    for dep in lines:
        if dep.startswith("/opt/homebrew/") or dep.startswith("/usr/local/"):
            raise SystemExit(f"Non-portable dependency in {binary}: {dep}")


def check_macos(bundle: Path) -> None:
    contents = bundle / "Contents"
    macos_dir = contents / "MacOS"
    exe = macos_dir / "SimsSDL"
    sdl_dylib = contents / "Frameworks" / "libSDL3.0.dylib"
    shaders_dir = contents / "Resources" / "Shaders"

    require_file(contents / "Info.plist", "Info.plist")
    require_file(exe, "macOS executable (SimsSDL)")
    require_file(contents / "Resources" / "AppIcon.icns", "AppIcon.icns")
    require_non_empty_shaders(shaders_dir, "bundled Shaders directory")
    require_file(sdl_dylib, "bundled libSDL3.0.dylib")

    for entry in macos_dir.iterdir():
        if entry.is_dir():
            raise SystemExit(f"Unexpected directory inside Contents/MacOS (breaks codesign): {entry}")

    for name in (
        "SimsSDL.dll",
        "SimsSDL.deps.json",
        "SimsSDL.runtimeconfig.json",
        "SimsSimulation.dll",
        "SimsEngine.dll",
        "SimsPluginApi.dll",
        "libhostfxr.dylib",
    ):
        require_file(macos_dir / name, f"macOS runtime file {name}")

    for mach in (exe, sdl_dylib):
        file_out = subprocess.run(["file", str(mach)], check=True, capture_output=True, text=True).stdout
        if "arm64" not in file_out:
            raise SystemExit(f"Expected arm64 Mach-O binary at {mach}, got: {file_out.strip()}")
        check_otool_dependencies(mach)


def check_windows(bundle: Path) -> None:
    require_file(bundle / "SimsSDL.exe", "Windows executable")
    require_file(bundle / "SDL3.dll", "bundled SDL3.dll")
    shaders = bundle / "Shaders"
    require_non_empty_shaders(shaders, "Windows Shaders directory")
    if not any(item.is_file() and item.suffix == ".dxil" for item in shaders.iterdir()):
        raise SystemExit(f"Missing D3D12 .dxil shaders in Windows Shaders directory: {shaders}")

    for name in (
        "SimsSDL.dll",
        "SimsSDL.deps.json",
        "SimsSDL.runtimeconfig.json",
        "SimsSimulation.dll",
        "SimsEngine.dll",
        "SimsPluginApi.dll",
        "hostfxr.dll",
    ):
        require_file(bundle / name, f"Windows runtime file {name}")


def check_linux(bundle: Path) -> None:
    require_file(bundle / "SimsSDL", "Linux executable")
    require_file(bundle / "libSDL3.so.0", "bundled libSDL3.so.0")
    require_non_empty_shaders(bundle / "Shaders", "Linux Shaders directory")

    for name in (
        "SimsSDL.dll",
        "SimsSDL.deps.json",
        "SimsSDL.runtimeconfig.json",
        "SimsSimulation.dll",
        "SimsEngine.dll",
        "SimsPluginApi.dll",
        "libhostfxr.so",
    ):
        require_file(bundle / name, f"Linux runtime file {name}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path, help="Path to The Sims Native.app or Windows/Linux dist directory")
    parser.add_argument("--platform", choices=("auto", "macos", "windows", "linux"), default="auto")
    parser.add_argument("--game", type=Path, action="append", default=[], help="Original Sims 1 game dir to verify zero overlap")
    args = parser.parse_args()

    platform = detect_platform(args.bundle, args.platform)
    check_common(args.bundle, args.game)
    if platform == "macos":
        check_macos(args.bundle)
    elif platform == "windows":
        check_windows(args.bundle)
    else:
        check_linux(args.bundle)
    print(f"Release bundle OK ({platform}): {args.bundle}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
