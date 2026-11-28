# Starts the consolidated Victory runtime, which incorporates the Victory-NVIDIA base and
# the newer upstream rendering fixes. It never downloads SDK files or modifies the game directory.
param(
    [Parameter(Mandatory)][string]$GameExe,
    [switch]$Preload,
    [switch]$UseDlss,
    [ValidateRange(1, 1000)][int]$Fps = 1000,
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = Join-Path $root 'src\victory'
$victory = Join-Path $repo 'target\release\victory.exe'

if (-not (Test-Path -LiteralPath $repo)) {
    throw 'Victory source is missing.'
}
if (-not (Test-Path -LiteralPath $GameExe)) {
    throw "Game executable not found: $GameExe"
}
$dlss = 'off'
if ($UseDlss) {
    $streamline = if ($env:VICTORY_STREAMLINE_DIR) { $env:VICTORY_STREAMLINE_DIR } else { Join-Path $repo 'nvidia-sdk\Streamline\bin\x64' }
    if (-not (Test-Path -LiteralPath (Join-Path $streamline 'sl.interposer.dll'))) {
        throw "DLSS was requested but Streamline is absent: $streamline. Set VICTORY_STREAMLINE_DIR to an existing SDK installation."
    }
    $env:VICTORY_STREAMLINE_DIR = $streamline
    $dlss = 'quality'
}

if ($Build) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Component victory
    if ($LASTEXITCODE -ne 0) { throw "Victory build failed: $LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath $victory)) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Component victory
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $victory)) { throw 'Victory build did not produce victory.exe.' }
}

Write-Host 'Victory safe profile: RTX 3070 / Series S / no frame generation / RT off.' -ForegroundColor Cyan
Write-Host 'Use -Preload only when you can wait for shader/pipeline preparation.' -ForegroundColor DarkCyan
Push-Location $repo
try {
    if ($Preload) {
        # The current fork exposes the two preloading phases separately.
        # Keep concurrency deliberately low on the RTX 3070 while its driver cache is cold.
        & $victory shaders --jobs 2 ([IO.Path]::GetDirectoryName($GameExe))
        if ($LASTEXITCODE -ne 0) { throw "Victory shader precompile failed: $LASTEXITCODE" }
        # Warm the pipelines observed by previous runs first. Building every
        # known permutation can take hours and delays the game more than it helps.
        & $victory warm --used --jobs 1
        if ($LASTEXITCODE -ne 0) { throw "Victory pipeline warm-up failed: $LASTEXITCODE" }
    }
    # `play` clears inherited VICTORY_* switches, so keep local overrides explicit.
    # The current upstream describes its RT path and water-light path as experimental;
    # keep both disabled on the 8 GB profile until they pass a stable local run.
    & $victory play $GameExe --series-s --dlss $dlss --fps $Fps --no-framegen-ready `
        --set VICTORY_ADAPTER=nvidia `
        --set VICTORY_TEXTURE_ROOM_PERCENT=85 `
        --set VICTORY_LEVELS_ON_DEMAND=1 `
        --set VICTORY_TEXTURE_STAGE_MS=8 `
        --set VICTORY_BVH_BUILDER= `
        --set VICTORY_WATER_LIGHT=
} finally {
    Pop-Location
}
