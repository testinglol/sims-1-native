#!/usr/bin/env bash
# Builds a standalone Linux x64 distribution directory & archive: dist/The-Sims-Native-Linux-x64.zip
# Contains the self-contained .NET 9 + SDL3 client, in-process simulation engine, and compiled SPIR-V shaders.
#   tools/package_linux.sh [VERSION]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:-0.2.0}"
dist="$root/dist"
stage_root="$root/.package-staging/linux-x64"
stage="$stage_root/The Sims Native"
zip_path="$dist/The-Sims-Native-Linux-x64.zip"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$root/runtime/dotnet}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$root/runtime/nuget}"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$dist" "$stage_root"

find_sdl3_so() {
  local candidates=()
  if [[ -n "${SDL3_LIB:-}" ]]; then
    candidates+=("$SDL3_LIB")
  fi
  if [[ -n "${SDL3_DIR:-}" ]]; then
    candidates+=(
      "$SDL3_DIR/lib/libSDL3.so.0"
      "$SDL3_DIR/lib64/libSDL3.so.0"
      "$SDL3_DIR/lib/libSDL3.so"
      "$SDL3_DIR/lib64/libSDL3.so"
    )
  fi
  candidates+=(
    "/usr/local/lib/libSDL3.so.0"
    "/usr/local/lib64/libSDL3.so.0"
    "/usr/lib/x86_64-linux-gnu/libSDL3.so.0"
    "/usr/lib64/libSDL3.so.0"
    "/usr/lib/libSDL3.so.0"
  )
  for c in "${candidates[@]}"; do
    if [[ -f "$c" ]]; then
      python3 -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$c"
      return 0
    fi
  done
  return 1
}

sdl_real="$(find_sdl3_so || true)"
if [[ -z "$sdl_real" || ! -f "$sdl_real" ]]; then
  echo "error: could not locate libSDL3.so.0 (set SDL3_DIR or SDL3_LIB)" >&2
  exit 1
fi

rm -rf "$stage"
mkdir -p "$stage"

dotnet restore "$root/native/SimsSDL/SimsSDL.csproj" -r linux-x64 \
  -p:EnableWindowsTargeting=true -p:NuGetAudit=false --disable-build-servers -nologo

dotnet clean "$root/native/SimsSDL/SimsSDL.csproj" -c Release -p:EnableWindowsTargeting=true --disable-build-servers -nologo >/dev/null
dotnet publish "$root/native/SimsSDL/SimsSDL.csproj" -c Release -r linux-x64 --self-contained true \
  --no-restore -p:EnableWindowsTargeting=true -p:NuGetAudit=false -p:DebugType=None -p:DebugSymbols=false \
  -p:Deterministic=true -p:PathMap="$root=/src" --disable-build-servers -o "$stage"

find "$stage" -type f \( -name '*.pdb' -o -name '*SDL2*' -o -name '*openal*' -o -name '*soft_oal*' \) -delete
rm -rf "$stage/runtimes"

cp -f "$sdl_real" "$stage/libSDL3.so.0"
chmod 755 "$stage/SimsSDL" "$stage/libSDL3.so.0"
if [[ -f "$root/dist/sims1-icon.png" ]]; then
  cp "$root/dist/sims1-icon.png" "$stage/sims1-icon.png"
fi

game_args=(--platform linux)
if [[ -d "$root/runtime/game" ]]; then game_args+=(--game "$root/runtime/game"); fi
if [[ -n "${SIMS_GAME_DIR:-}" ]]; then game_args+=(--game "$SIMS_GAME_DIR"); fi
python3 "$root/tools/checks/check_release_bundle.py" "$stage" "${game_args[@]}"

rm -f "$zip_path"
python3 - "$stage" "$zip_path" <<'EOF'
import os, pathlib, sys, zipfile

stage = pathlib.Path(sys.argv[1]).resolve()
zip_path = pathlib.Path(sys.argv[2]).resolve()
parent = stage.parent

with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
    for item in sorted(stage.rglob("*")):
        rel = item.relative_to(parent).as_posix()
        if item.is_dir():
            continue
        info = zipfile.ZipInfo.from_file(item, arcname=rel)
        mode = item.stat().st_mode & 0o777
        if item.name in ("SimsSDL", "libSDL3.so.0"):
            mode = 0o755
        info.external_attr = (0o100000 | mode) << 16
        zf.writestr(info, item.read_bytes(), compress_type=zipfile.ZIP_DEFLATED)
EOF

rm -rf "$stage_root"
echo "Built $zip_path ($version)"
