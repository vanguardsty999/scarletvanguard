param([switch]$Strict)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$problems = 0

function Check-Item([bool]$ok, [string]$message) {
    if ($ok) { Write-Host "  OK   $message" -ForegroundColor Green }
    else { Write-Host "  FAIL $message" -ForegroundColor Red; $script:problems++ }
}

Write-Host "XRMLX-Fusion validation - $root" -ForegroundColor Cyan

$markers = @{
    'leonida'      = @('make_game.py', 'LICENSE')
    'xcl'          = @('Cargo.toml', 'LICENSE')
    'victory-fork' = @('Cargo.toml', 'LICENSE', '.victory-root')
}

foreach ($name in @('leonida', 'xcl', 'victory-fork')) {
    $dir = Join-Path $root $name
    Write-Host "`n[$name]" -ForegroundColor Cyan
    Check-Item (Test-Path $dir) 'source directory present'
    if (-not (Test-Path $dir)) { continue }
    foreach ($marker in $markers[$name]) {
        Check-Item (Test-Path (Join-Path $dir $marker)) "marker '$marker' present"
    }
}

Write-Host "`n[preparation docs/scripts]" -ForegroundColor Cyan
$prep = @(
    'docs\PLATFORM_ABSTRACTION.md', 'docs\PERF_RUNBOOK.md',
    'docs\HARDWARE_PROFILE_RTX3070.md', 'docs\XBOX_PARTICULARITIES.md',
    'scripts\probe-game.ps1', 'scripts\pso-precompile.ps1',
    'scripts\measure.ps1', 'scripts\affinity.ps1',
    'configs\rtx3070-24gb.env.ps1', 'scripts\optimize-rtx3070.ps1',
    'scripts\build-launcher.ps1', 'scripts\launcher\ScarletVanguardLauncher.cs',
    'scripts\victory-nvidia.ps1'
)
foreach ($item in $prep) { Check-Item (Test-Path (Join-Path $root $item)) "$item present" }

Write-Host "`n[flattening]" -ForegroundColor Cyan
$rootGit = Join-Path $root '.git'
$nested = Get-ChildItem $root -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq '.git' -and $_.FullName -ne $rootGit -and -not $_.FullName.StartsWith($rootGit + [IO.Path]::DirectorySeparatorChar) }
Check-Item ($null -eq $nested) "no internal .git/submodule metadata ($(@($nested).Count) found)"

Write-Host "`n[policy: no game data]" -ForegroundColor Cyan
$forbidden = Get-ChildItem $root -Recurse -File -Force -Include '*.exe','*.dll','*.rpf','*.xvc','*.msixvc','*.ttpf','*.bk2','*.awc','*.bvh4' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(target|build|bin)\\' }
Check-Item ($null -eq $forbidden) "no executables, libraries, or game packages outside build output ($(@($forbidden).Count) files)"

Write-Host "`n[build tools]" -ForegroundColor Cyan
. (Join-Path $PSScriptRoot 'msvc.ps1')
try { Initialize-Msvc } catch { Write-Host "  MSVC: $($_.Exception.Message)" -ForegroundColor Yellow }
foreach ($tool in @('git','python','cargo','rustc','cl','link','cmake','dotnet','spirv-val','llvm-mc')) {
    $found = $null -ne (Get-Command $tool -ErrorAction SilentlyContinue)
    Write-Host "  $tool`: $(if ($found) { 'available' } else { 'MISSING' })"
}

Write-Host "`nResult: $problems problem(s)" -ForegroundColor $(if ($problems -eq 0) { 'Green' } else { 'Red' })
if ($Strict -and $problems -gt 0) { exit 1 }
