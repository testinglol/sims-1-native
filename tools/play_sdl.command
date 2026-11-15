#!/bin/zsh
# Builds SimsSDL (C# + in-process SimsSimulation/SimsEngine), then launches the client.
# Extra arguments go to SimsSDL, for example `--house 5`.
set -eu
task_root="${0:A:h:h}"
task_game="${SIMS_GAME_DIR:-$task_root/runtime/game}"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$task_root/runtime/dotnet}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$task_root/runtime/nuget}"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"
if [[ ! -f "$task_root/native/SimsSDL/obj/project.assets.json" ]]; then
  dotnet restore "$task_root/native/SimsSDL/SimsSDL.csproj" -m:1 --disable-build-servers -p:EnableWindowsTargeting=true -p:NuGetAudit=false -nologo
fi
dotnet build "$task_root/native/SimsSDL/SimsSDL.csproj" -c Release -m:1 --no-restore --disable-build-servers -p:EnableWindowsTargeting=true -p:UseSharedCompilation=false -nologo -tl:off -clp:ErrorsOnly
cd "$task_root"
exec dotnet "$task_root/native/SimsSDL/bin/Release/net9.0/SimsSDL.dll" --workspace "$task_root" --game "$task_game" --packs "$task_root/packs" "$@"
