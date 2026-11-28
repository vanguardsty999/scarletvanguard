param(
    [ValidateSet('xcl','victory')]
    [string]$Component = 'xcl',
    [string]$GamePath = '',
    [ValidateSet('pix','vtune','etw','all')]
    [string]$Tool = 'all',
    [string]$SessionDir = '',
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'msvc.ps1')
Initialize-Msvc

if ([string]::IsNullOrWhiteSpace($SessionDir)) {
    $SessionDir = Join-Path $root ("measurements\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $SessionDir | Out-Null

# NVIDIA profile + trace variables. Nothing here touches the game directory.
# HOST_WAVE/HOST_MAX_WAVE are translate-only; runtime measurement does not use them.
. (Join-Path $PSScriptRoot '..\configs\rtx3070-24gb.env.ps1')
Set-ProfileEnv -Preset safe -Trace

Write-Host "Measurement session: $SessionDir" -ForegroundColor Cyan
Write-Host "Component: $Component | Tools: $Tool" -ForegroundColor Cyan
Write-Host 'Variables applied to this process:' -ForegroundColor Yellow
Get-ChildItem Env: | Where-Object { $_.Name -match '^(XCL_|VICTORY_)' } |
    Sort-Object Name | Format-Table -AutoSize

function Find-Tool([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$commands = @()
$commands += "# Session $(Get-Date -Format o) - component $Component"
$commands += "# Session folder: $SessionDir"

if ($Tool -in @('pix','all')) {
    $pix = Find-Tool 'PIX'
    if ($pix) { $commands += "PIX: $pix" }
    else { $commands += 'PIX: not on PATH. Install Microsoft PIX and attach to the process.' }
    $commands += 'PIX: capture 1 frame cold-cache and 1 warm-cache; look for Create*PipelineState inside the frame.'
}

if ($Tool -in @('vtune','all')) {
    $vtune = Find-Tool 'vtune'
    if (-not $vtune) {
        $candidate = 'C:\Program Files\Intel\VTune Amplifier\bin64\vtune.exe'
        if (Test-Path $candidate) { $vtune = $candidate }
    }
    if ($vtune) { $commands += "VTune: $vtune" }
    else { $commands += 'VTune: not found. Install Intel VTune Profiler.' }
    $commands += 'VTune: Hotspots for 60-120s, then Threading for wait/contention/imbalance.'
}

if ($Tool -in @('etw','all')) {
    $wpr = Find-Tool 'wpr'
    $xperf = Find-Tool 'xperf'
    $commands += "wpr: $(if ($wpr) { $wpr } else { 'NOT FOUND (Windows ADK / WPT)' })"
    $commands += "xperf: $(if ($xperf) { $xperf } else { 'NOT FOUND (Windows ADK / WPT)' })"
    if ($wpr) {
        $trace = Join-Path $SessionDir 'trace.etl'
        $commands += 'wpr -start CPU -start GPU -filemode'
        $commands += '# ... run the game with the variables above ...'
        $commands += "wpr -stop `"$trace`""
        $commands += "xperf -i `"$trace`" -o `"$SessionDir\summary.txt`" -a profile"
    }
}

$commands += ''
$commands += '# Launch commands (only when authorized):'
if ($Component -eq 'xcl') {
    $exe = Join-Path $root 'src\xcl\target\release\xcl.exe'
    $commands += "`"$exe`" run `"$GamePath`""
} else {
    $exe = Join-Path $root 'src\victory\target\release\victory.exe'
    $commands += "`"$exe`" run `"$GamePath`" --execute"
}

$out = Join-Path $SessionDir 'commands.txt'
$commands | Set-Content -LiteralPath $out -Encoding UTF8
Write-Host "`nCommands saved to: $out" -ForegroundColor Green
Write-Host 'No game was launched. Use -Launch if you want to start it.' -ForegroundColor Yellow

if ($Launch) {
    if ([string]::IsNullOrWhiteSpace($GamePath)) { throw '-Launch requires -GamePath.' }
    if (-not (Test-Path -LiteralPath $GamePath)) { throw "GamePath not found: $GamePath" }
    if (-not (Test-Path -LiteralPath $exe)) { throw "Executable missing: $exe" }
    if ($Component -eq 'xcl') { & $exe run $GamePath }
    else { & $exe run $GamePath --execute }
}
