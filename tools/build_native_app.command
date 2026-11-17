#!/bin/zsh
set -euo pipefail
export TMPDIR=/tmp
task_root="${0:A:h:h}"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$task_root/runtime/dotnet}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$task_root/runtime/nuget}"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

publish_dir="$task_root/native/SimsSDL/bin/Release/net9.0/osx-arm64/publish"
if [[ ! -f "$task_root/native/SimsSDL/obj/project.assets.json" ]] || ! grep -q "osx-arm64" "$task_root/native/SimsSDL/obj/project.assets.json"; then
  dotnet restore "$task_root/native/SimsSDL/SimsSDL.csproj" -r osx-arm64 -m:1 --disable-build-servers \
    -p:EnableWindowsTargeting=true -p:NuGetAudit=false -nologo
fi

rm -rf "$publish_dir"
dotnet publish "$task_root/native/SimsSDL/SimsSDL.csproj" -c Release -r osx-arm64 --self-contained true \
  -m:1 --no-restore --disable-build-servers -p:EnableWindowsTargeting=true -p:NuGetAudit=false \
  -p:UseSharedCompilation=false -p:DebugType=None -p:DebugSymbols=false -nologo -tl:off -clp:ErrorsOnly \
  -o "$publish_dir"

find "$publish_dir" -type f \( -name '*.pdb' -o -name '*SDL2*' -o -name '*openal*' -o -name '*soft_oal*' \) -delete
rm -rf "$publish_dir/runtimes"

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

task_build_app() {
  local app_path="$1"
  local info_plist="$2"
  rm -rf "$app_path/Contents/MacOS" "$app_path/Contents/Frameworks" "$app_path/Contents/Resources/Shaders"
  mkdir -p "$app_path/Contents/MacOS" "$app_path/Contents/Frameworks" "$app_path/Contents/Resources"

  find "$publish_dir" -maxdepth 1 -type f -exec cp {} "$app_path/Contents/MacOS/" \;
  chmod 755 "$app_path/Contents/MacOS/SimsSDL"

  if [[ ! -d "$publish_dir/Shaders" ]]; then
    echo "error: missing published Shaders directory at $publish_dir/Shaders" >&2
    exit 1
  fi
  cp -R "$publish_dir/Shaders" "$app_path/Contents/Resources/Shaders"

  cp -f "$sdl_real" "$app_path/Contents/Frameworks/libSDL3.0.dylib"
  chmod 755 "$app_path/Contents/Frameworks/libSDL3.0.dylib"
  install_name_tool -id "@rpath/libSDL3.0.dylib" "$app_path/Contents/Frameworks/libSDL3.0.dylib"

  if [[ -f "$task_root/dist/sims1-icon.icns" ]]; then
    cp "$task_root/dist/sims1-icon.icns" "$app_path/Contents/Resources/AppIcon.icns"
  fi
  if [[ -d "$task_root/runtime/ui-hd-pack" ]]; then
    rm -rf "$app_path/Contents/Resources/HdUi"
    cp -R "$task_root/runtime/ui-hd-pack" "$app_path/Contents/Resources/HdUi"
  fi
  cp "$info_plist" "$app_path/Contents/Info.plist"
  codesign --force --deep --sign - "$app_path"
  print "Built $app_path"
}

task_build_app "$task_root/Sims Native Preview.app" "$task_root/native/SimsSDL/PreviewInfo.plist"
