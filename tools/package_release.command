#!/usr/bin/env bash
# Builds a standalone Apple Silicon app bundle: dist/Plumbob Engine.app
# It contains the unified self-contained .NET 9 + SDL3 client and in-process simulation engine, with zero game assets.
# Players choose their own installed game folder on first launch.
#   tools/package_release.command [VERSION]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-0.5.2}"
build="$(date +%Y%m%d%H%M)"
dist="$root/dist"
target_app="$dist/Plumbob Engine.app"
work="$root/.package-staging/release-$build"
app="$work/Plumbob Engine.app"
publish_dir="$work/publish"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$root/runtime/dotnet}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$root/runtime/nuget}"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

rm -rf "$app" "$publish_dir"
mkdir -p "$dist" "$work" "$publish_dir"

dotnet restore "$root/native/SimsSDL/SimsSDL.csproj" -r osx-arm64 \
  -p:EnableWindowsTargeting=true -p:NuGetAudit=false --disable-build-servers -nologo

dotnet clean "$root/native/SimsSDL/SimsSDL.csproj" -c Release -p:EnableWindowsTargeting=true --disable-build-servers -nologo >/dev/null
dotnet publish "$root/native/SimsSDL/SimsSDL.csproj" -c Release -r osx-arm64 --self-contained true \
  --no-restore -p:EnableWindowsTargeting=true -p:NuGetAudit=false -p:DebugType=None -p:DebugSymbols=false \
  -p:Deterministic=true -p:PathMap="$root=/src" --disable-build-servers -o "$publish_dir"

find "$publish_dir" -type f \( -name '*.pdb' -o -name '*SDL2*' -o -name '*openal*' -o -name '*soft_oal*' \) -delete
rm -rf "$publish_dir/runtimes"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$app/Contents/Frameworks"
find "$publish_dir" -maxdepth 1 -type f -exec cp {} "$app/Contents/MacOS/" \;
chmod 755 "$app/Contents/MacOS/SimsSDL"
if [[ ! -d "$publish_dir/Shaders" ]]; then
  echo "error: missing published Shaders directory at $publish_dir/Shaders" >&2
  exit 1
fi
cp -R "$publish_dir/Shaders" "$app/Contents/Resources/Shaders"
mkdir -p "$app/Contents/Resources/Packs"
for pack_name in lighting seasons; do
  pack_src="$root/packs/$pack_name"
  if [[ ! -f "$pack_src/pack.json" ]]; then
    echo "error: missing bundled pack at $pack_src" >&2
    exit 1
  fi
  cp -R "$pack_src" "$app/Contents/Resources/Packs/$pack_name"
  rm -rf "$app/Contents/Resources/Packs/$pack_name/shaders-hlsl"
  find "$app/Contents/Resources/Packs/$pack_name" -name '.DS_Store' -delete
done
if [[ -f "$root/dist/sims1-icon.icns" ]]; then
  cp "$root/dist/sims1-icon.icns" "$app/Contents/Resources/AppIcon.icns"
fi
sed -e "s/__VERSION__/$version/" -e "s/__BUILD__/$build/" "$root/native/SimsSDL/ReleaseInfo.plist" > "$app/Contents/Info.plist"

sdl_source="${SDL3_DYLIB:-}"
if [[ -z "$sdl_source" || ! -f "$sdl_source" ]]; then
  sdl_prefix="$(brew --prefix sdl3 2>/dev/null || true)"
  if [[ -n "$sdl_prefix" && -f "$sdl_prefix/lib/libSDL3.0.dylib" ]]; then
    sdl_source="$sdl_prefix/lib/libSDL3.0.dylib"
  elif [[ -f "/opt/homebrew/opt/sdl3/lib/libSDL3.0.dylib" ]]; then
    sdl_source="/opt/homebrew/opt/sdl3/lib/libSDL3.0.dylib"
  elif [[ -f "/opt/homebrew/lib/libSDL3.0.dylib" ]]; then
    sdl_source="/opt/homebrew/lib/libSDL3.0.dylib"
  elif [[ -f "/usr/local/opt/sdl3/lib/libSDL3.0.dylib" ]]; then
    sdl_source="/usr/local/opt/sdl3/lib/libSDL3.0.dylib"
  fi
fi
if [[ -z "$sdl_source" || ! -f "$sdl_source" ]]; then
  echo "error: could not locate libSDL3.0.dylib (set SDL3_DYLIB or install with: brew install sdl3)" >&2
  exit 1
fi
sdl_real="$(python3 -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$sdl_source")"
cp -f "$sdl_real" "$app/Contents/Frameworks/libSDL3.0.dylib"
chmod 755 "$app/Contents/Frameworks/libSDL3.0.dylib"

if command -v install_name_tool >/dev/null 2>&1; then
  install_name_tool -id "@rpath/libSDL3.0.dylib" "$app/Contents/Frameworks/libSDL3.0.dylib"
elif command -v llvm-install-name-tool >/dev/null 2>&1; then
  llvm-install-name-tool -id "@rpath/libSDL3.0.dylib" "$app/Contents/Frameworks/libSDL3.0.dylib"
elif command -v llvm-install-name-tool-18 >/dev/null 2>&1; then
  llvm-install-name-tool-18 -id "@rpath/libSDL3.0.dylib" "$app/Contents/Frameworks/libSDL3.0.dylib"
else
  echo "error: neither install_name_tool nor llvm-install-name-tool found" >&2
  exit 1
fi

if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$app"
elif command -v rcodesign >/dev/null 2>&1; then
  rcodesign sign "$app"
else
  echo "error: neither codesign nor rcodesign found" >&2
  exit 1
fi

game_args=(--platform macos)
if [[ -d "$root/runtime/game" ]]; then game_args+=(--game "$root/runtime/game"); fi
if [[ -n "${SIMS_GAME_DIR:-}" ]]; then game_args+=(--game "$SIMS_GAME_DIR"); fi
python3 "$root/tools/checks/check_release_bundle.py" "$app" "${game_args[@]}"
if [[ -e "$target_app" ]]; then mv "$target_app" "$work/previous-Plumbob Engine.app"; fi
mv "$app" "$target_app"
zip_path="$dist/Plumbob-Engine-macOS-arm64.zip"
rm -f "$zip_path"
if command -v ditto >/dev/null 2>&1; then
  ditto -c -k --sequesterRsrc --keepParent "$target_app" "$zip_path"
else
  python3 - "$target_app" "$zip_path" <<'EOF'
import pathlib, sys, zipfile

bundle = pathlib.Path(sys.argv[1]).resolve()
zip_path = pathlib.Path(sys.argv[2]).resolve()
parent = bundle.parent

with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
    for item in sorted(bundle.rglob("*")):
        rel = item.relative_to(parent).as_posix()
        if item.is_dir():
            continue
        info = zipfile.ZipInfo.from_file(item, arcname=rel)
        mode = item.stat().st_mode & 0o777
        if item.name == "SimsSDL" or item.suffix == ".dylib":
            mode = 0o755
        info.external_attr = (0o100000 | mode) << 16
        zf.writestr(info, item.read_bytes(), compress_type=zipfile.ZIP_DEFLATED)
EOF
fi
rm -rf "$work"
echo "Built $target_app and $zip_path"
