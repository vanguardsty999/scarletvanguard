param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir,
    [ValidateSet('xcl','victory','both')]
    [string]$Component = 'both',
    [string]$OutFile = '',
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'msvc.ps1')
Initialize-Msvc

if (-not (Test-Path -LiteralPath $GameDir)) { throw "Game directory not found: $GameDir" }
if ([string]::IsNullOrWhiteSpace($OutFile)) { $OutFile = Join-Path $root 'precompile-commands.txt' }

. (Join-Path $PSScriptRoot '..\configs\rtx3070-24gb.env.ps1')
# Precompilation benefits from the same DXC path/thread limits as the runtime.
Set-ProfileEnv -Preset safe
# HOST_WAVE / HOST_MAX_WAVE are read only by `victory translate`, not by `victory run`.
Set-TranslateEnv

Write-Host "PROBE + PREPARE - no game file will be written." -ForegroundColor Cyan
$packs = @(Get-ChildItem -LiteralPath $GameDir -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -ieq '.rpf' } |
    Select-Object -First 200)

Write-Host "RPF packages found: $($packs.Count)" -ForegroundColor Yellow

$lines = @()
$lines += "# PSO precompile commands - generated $(Get-Date -Format o)"
$lines += "# Game: $GameDir"
$lines += '# Caches stay in the XCL/Victory data or cache folder, never in the game.'
$lines += ''

$xclExe = Join-Path $root 'src\xcl\target\release\xcl.exe'
$vicExe = Join-Path $root 'src\victory\target\release\victory.exe'

if ($Component -in @('xcl','both')) {
    $lines += '## XCL'
    foreach ($p in $packs) {
        $lines += "`"$xclExe`" shaders `"$($p.FullName)`" --precompile --pipelines"
        $lines += '# per-stage alternative: --precompile --only cs --pipelines'
    }
    $lines += ''
}

if ($Component -in @('victory','both')) {
    $lines += '## Victory'
    $lines += "`"$vicExe`" translate `"$GameDir`" --validate --pipelines"
    foreach ($p in $packs) {
        $lines += "# `"$vicExe`" translate `"$($p.FullName)`" --validate --pipelines"
    }
    $lines += ''
}

$lines | Set-Content -LiteralPath $OutFile -Encoding UTF8
Write-Host "Commands saved to: $OutFile" -ForegroundColor Green

if ($Execute) {
    Write-Host 'Running precompilation...' -ForegroundColor Yellow
    if ($Component -in @('xcl','both')) {
        if (-not (Test-Path -LiteralPath $xclExe)) { throw "Executable missing: $xclExe" }
        foreach ($p in $packs) { & $xclExe shaders $p.FullName --precompile --pipelines }
    }
    if ($Component -in @('victory','both')) {
        if (-not (Test-Path -LiteralPath $vicExe)) { throw "Executable missing: $vicExe" }
        & $vicExe translate $GameDir --validate --pipelines
    }
} else {
    Write-Host 'Use -Execute when the download finishes and you authorize precompilation.' -ForegroundColor Yellow
}
