param(
    [string]$Version = "0.1.0",
    [string]$SdlDir = $env:SDL3_DIR,
    [string]$GameDir = $env:SIMS_GAME_DIR
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Dist = Join-Path $Root "dist"
$StageRoot = Join-Path $Root ".package-staging\windows-x64"
$Stage = Join-Path $StageRoot "The Sims Native"
$ZipPath = Join-Path $Dist "The-Sims-Native-Windows-x64.zip"

if ([string]::IsNullOrWhiteSpace($SdlDir) -or -not (Test-Path $SdlDir)) {
    throw "SDL3 directory not found. Pass -SdlDir or set SDL3_DIR (got '$SdlDir')."
}

$SdlDll = @(
    (Join-Path $SdlDir "lib\x64\SDL3.dll"),
    (Join-Path $SdlDir "bin\SDL3.dll"),
    (Join-Path $SdlDir "lib\SDL3.dll"),
    (Join-Path $SdlDir "SDL3.dll")
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $SdlDll) {
    $FoundDll = Get-ChildItem -Path $SdlDir -Recurse -Filter "SDL3.dll" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch 'arm64|x86' } |
        Select-Object -First 1
    if (-not $FoundDll) {
        $FoundDll = Get-ChildItem -Path $SdlDir -Recurse -Filter "SDL3.dll" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if ($FoundDll) {
        $SdlDll = $FoundDll.FullName
    }
}
if (-not $SdlDll) {
    throw "SDL3.dll not found under $SdlDir"
}

if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Path $Dist -Force | Out-Null
New-Item -ItemType Directory -Path $Stage -Force | Out-Null

Write-Host "Publishing self-contained Windows SimsSDL client + simulation engine ($Version)..."
$Project = Join-Path $Root "native\SimsSDL\SimsSDL.csproj"
& dotnet restore $Project -r win-x64 -p:EnableWindowsTargeting=true -p:NuGetAudit=false --disable-build-servers -nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE"
}
& dotnet clean $Project -c Release -p:EnableWindowsTargeting=true --disable-build-servers -nologo | Out-Null
& dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:EnableWindowsTargeting=true `
    -p:NuGetAudit=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Deterministic=true `
    -p:PathMap="$Root=/src" `
    --disable-build-servers `
    -o $Stage
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Get-ChildItem -Path $Stage -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -ieq ".pdb" -or $_.Name -match 'SDL2|openal|soft_oal' } |
    Remove-Item -Force
$UnusedRuntimes = Join-Path $Stage "runtimes"
if (Test-Path $UnusedRuntimes) {
    Remove-Item $UnusedRuntimes -Recurse -Force
}

Copy-Item $SdlDll (Join-Path $Stage "SDL3.dll") -Force

$IconPng = Join-Path $Root "dist\sims1-icon.png"
if (Test-Path $IconPng) {
    Copy-Item $IconPng (Join-Path $Stage "sims1-icon.png") -Force
}

$GameArgs = @("--platform", "windows")
if (-not [string]::IsNullOrWhiteSpace($GameDir) -and (Test-Path $GameDir)) {
    $GameArgs += @("--game", $GameDir)
}
$LocalGame = Join-Path $Root "runtime\game"
if (Test-Path $LocalGame) {
    $GameArgs += @("--game", $LocalGame)
}

& python (Join-Path $Root "tools\checks\check_release_bundle.py") $Stage @GameArgs
if ($LASTEXITCODE -ne 0) {
    throw "check_release_bundle.py failed with exit code $LASTEXITCODE"
}

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path $Stage -DestinationPath $ZipPath -Force
Write-Host "Built $Stage and $ZipPath ($Version)"
