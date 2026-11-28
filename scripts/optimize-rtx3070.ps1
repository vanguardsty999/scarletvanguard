param(
    [ValidateSet('safe','speed')]
    [string]$Preset = 'safe',
    [ValidateSet('none','xcl','victory-fork')]
    [string]$Component = 'xcl',
    [string]$GamePath = '',
    [switch]$Trace,
    [switch]$ApplyAffinity,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'msvc.ps1')
. (Join-Path $PSScriptRoot '..\configs\rtx3070-24gb.env.ps1')
Initialize-Msvc

function Test-Hardware {
    Write-Host "`n[hardware]" -ForegroundColor Cyan
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $os  = Get-CimInstance Win32_OperatingSystem
    $gpu = Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' } | Select-Object -First 1
    $ram = [math]::Round($os.TotalVisibleMemorySize / 1MB, 2)

    Write-Host "  CPU: $($cpu.Name) ($($cpu.NumberOfCores)c/$($cpu.NumberOfLogicalProcessors)t)"
    Write-Host "  RAM: $ram GB (free $([math]::Round($os.FreePhysicalMemory / 1MB, 2)) GB)"
    if ($gpu) { Write-Host "  GPU: $($gpu.Name)" }
    else { Write-Warning 'No NVIDIA GPU detected; XCL_ADAPTER/VICTORY_ADAPTER=nvidia may fail.' }
    if ($ram -lt 20) { Write-Warning 'Less than 20 GB RAM; use -Preset safe.' }
    if ($gpu -and $gpu.Name -notmatch '3070') { Write-Warning 'This profile was written for an RTX 3070; re-measure on other GPUs.' }
}

function Get-CoreMask([string]$spec) {
    $count = [Environment]::ProcessorCount
    $bits = New-Object 'bool[]' $count
    foreach ($part in $spec.Split(',')) {
        $part = $part.Trim()
        if ($part -match '^(\d+)-(\d+)$') {
            $from = [int]$Matches[1]; $to = [int]$Matches[2]
            for ($i = $from; $i -le $to; $i++) { if ($i -ge 0 -and $i -lt $count) { $bits[$i] = $true } }
        } elseif ($part -match '^\d+$') {
            $i = [int]$Matches[0]
            if ($i -ge 0 -and $i -lt $count) { $bits[$i] = $true }
        }
    }
    $mask = [UInt64]0
    for ($i = 0; $i -lt $count -and $i -lt 64; $i++) {
        if ($bits[$i]) { $mask = $mask -bor ([UInt64]1 -shl $i) }
    }
    return $mask
}

Test-Hardware
Set-ProfileEnv -Preset $Preset -Trace:$Trace
Show-ProfilePlan

Write-Host "`n[preset: $Preset]" -ForegroundColor Cyan
Get-ChildItem Env: | Where-Object { $_.Name -match '^(XCL_|VICTORY_)' } |
    Sort-Object Name | Format-Table -AutoSize

$exe = $null
$argLine = ''
if ($Component -ne 'none') {
    if ($Component -eq 'xcl') {
        $exe = Join-Path $root 'src\xcl\target\release\xcl.exe'
        $argLine = 'run "' + $GamePath + '"'
    } else {
        $exe = Join-Path $root 'src\victory-fork\target\release\VanguardScarletX.exe'
        $argLine = 'run "' + $GamePath + '" --execute'
    }
}

Write-Host "`n[command]" -ForegroundColor Cyan
if (-not $exe) {
    Write-Host '  (no component selected)'
} elseif (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "  BUILD MISSING: $exe" -ForegroundColor Red
} else {
    Write-Host "  `"$exe`" $argLine"
}

if (-not $Launch) {
    Write-Host "`nNo game launched. Use -Launch when the .exe is present and you authorize it." -ForegroundColor Yellow
    return
}

if ($Component -eq 'none') { throw '-Launch requires -Component xcl or victory-fork.' }
if ([string]::IsNullOrWhiteSpace($GamePath)) { throw '-Launch requires -GamePath.' }
if (-not (Test-Path -LiteralPath $GamePath)) { throw "Game executable not found: $GamePath" }
if (-not (Test-Path -LiteralPath $exe)) { throw "Build missing: $exe" }

foreach ($dir in @($Profile.Paths.XclDataRoot, $Profile.Paths.VictoryCacheDir, $Profile.Paths.VictoryDataDir)) {
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Write-Host "Created cache/data dir: $dir" -ForegroundColor Green
    }
}

$proc = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $root -PassThru
Write-Host "Launched PID $($proc.Id)" -ForegroundColor Green

if ($ApplyAffinity) {
    $mask = Get-CoreMask $Profile.Affinity.JobRenderMask
    $proc.ProcessorAffinity = [IntPtr][long]$mask
    Write-Host ("Process affinity set to 0x" + $mask.ToString('X') + " (driver/PSO cores left free: " + $Profile.Affinity.DriverMask + ")") -ForegroundColor Green
}
