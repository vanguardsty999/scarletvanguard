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
    'victory-fork' = @('Cargo.toml', 'LICENSE')
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

Write-Host "`n[flattening]" -ForegroundColor Cyan
$nested = Get-ChildItem $root -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq '.git' }
Check-Item ($null -eq $nested) "no internal .git/submodule metadata ($(@($nested).Count) found)"

Write-Host "`n[policy: no game data]" -ForegroundColor Cyan
$forbidden = Get-ChildItem $root -Recurse -File -Force -Include '*.exe','*.rpf','*.xvc','*.msixvc','*.ttpf','*.bk2','*.awc','*.bvh4' -ErrorAction SilentlyContinue
Check-Item ($null -eq $forbidden) "no game executables/packages found ($(@($forbidden).Count) files)"

Write-Host "`n[build tools]" -ForegroundColor Cyan
foreach ($tool in @('git','python','cargo','cmake','dotnet','spirv-val','llvm-mc')) {
    $found = $null -ne (Get-Command $tool -ErrorAction SilentlyContinue)
    Write-Host "  $tool`: $(if ($found) { 'available' } else { 'MISSING' })"
}

Write-Host "`nResult: $problems problem(s)" -ForegroundColor $(if ($problems -eq 0) { 'Green' } else { 'Red' })
if ($Strict -and $problems -gt 0) { exit 1 }
