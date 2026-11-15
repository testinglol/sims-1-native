#!/bin/sh
# Shader bindings: https://wiki.libsdl.org/SDL3/SDL_CreateGPUShader
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
source="$root/native/SimsSDL/Shaders/sdl"
output="$root/native/SimsSDL/Shaders"
mkdir -p "$output"
for shader in "$source"/*.vert "$source"/*.frag; do
    name=$(basename "$shader")
    glslangValidator -V --quiet -I"$source" -o "$output/$name.spv" "$shader"
done
python3 "$root/tools/build_sdl_msl_shaders.py"
