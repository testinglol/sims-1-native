#!/bin/zsh
# Builds a standalone Apple Silicon app bundle: dist/The Sims Native.app
# It contains the unified self-contained .NET 9 + SDL3 client and in-process simulation engine, with zero game assets.
# Players choose their own installed game folder on first launch.
#   tools/package_release.command [VERSION]
set -euo pipefail
root="${0:A:h:h}"
version="${1:-1.0.0}"
build="$(date +%Y%m%d%H%M)"
dist="$root/dist"
target_app="$dist/The Sims Native.app"
work="$root/.package-staging/release-$build"
app="$work/The Sims Native.app"
publish_dir="$work/publish"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$root/runtime/dotnet}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$root/runtime/nuget}"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

rm -rf "$app" "$publish_dir"
mkdir -p "$dist" "$work" "$publish_dir"

if [[ ! -f "$root/native/SimsSDL/obj/project.assets.json" ]] || ! grep -q "osx-arm64" "$root/native/SimsSDL/obj/project.assets.json"; then
  dotnet restore "$root/native/SimsSDL/SimsSDL.csproj" -r osx-arm64 \
    -p:EnableWindowsTargeting=true -p:NuGetAudit=false --disable-build-servers -nologo
fi

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
if [[ -f "$root/dist/sims1-icon.icns" ]]; then
  cp "$root/dist/sims1-icon.icns" "$app/Contents/Resources/AppIcon.icns"
fi
sed -e "s/__VERSION__/$version/" -e "s/__BUILD__/$build/" "$root/native/SimsSDL/ReleaseInfo.plist" > "$app/Contents/Info.plist"

sdl_source=""
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
if [[ -z "$sdl_source" || ! -f "$sdl_source" ]]; then
  echo "error: could not locate libSDL3.0.dylib (install with: brew install sdl3)" >&2
  exit 1
fi
sdl_real="$(python3 -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$sdl_source")"
cp -f "$sdl_real" "$app/Contents/Frameworks/libSDL3.0.dylib"
chmod 755 "$app/Contents/Frameworks/libSDL3.0.dylib"
install_name_tool -id "@rpath/libSDL3.0.dylib" "$app/Contents/Frameworks/libSDL3.0.dylib"

codesign --force --deep --sign - "$app"

game_args=(--platform macos)
if [[ -d "$root/runtime/game" ]]; then game_args+=(--game "$root/runtime/game"); fi
if [[ -n "${SIMS_GAME_DIR:-}" ]]; then game_args+=(--game "$SIMS_GAME_DIR"); fi
python3 "$root/tools/checks/check_release_bundle.py" "$app" "${game_args[@]}"
if [[ -e "$target_app" ]]; then mv "$target_app" "$work/previous-The Sims Native.app"; fi
mv "$app" "$target_app"
zip_path="$dist/The-Sims-Native-macOS-arm64.zip"
rm -f "$zip_path"
ditto -c -k --sequesterRsrc --keepParent "$target_app" "$zip_path"
rm -rf "$work"
echo "Built $target_app and $zip_path"
