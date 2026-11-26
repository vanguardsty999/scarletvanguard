param(
    [Parameter(Mandatory = $true)]
    [string]$ProcessName,
    [string]$Cores = '',
    [switch]$Threads,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

function Parse-Cores([string]$spec, [int]$count) {
    $bits = New-Object 'bool[]' $count
    foreach ($part in $spec.Split(',')) {
        $part = $part.Trim()
        if ([string]::IsNullOrWhiteSpace($part)) { continue }
        if ($part -match '^(\d+)-(\d+)$') {
            $from = [int]$Matches[1]
            $to = [int]$Matches[2]
            for ($i = $from; $i -le $to; $i++) {
                if ($i -ge 0 -and $i -lt $count) { $bits[$i] = $true }
            }
        } elseif ($part -match '^\d+$') {
            $i = [int]$Matches[0]
            if ($i -ge 0 -and $i -lt $count) { $bits[$i] = $true }
        } else {
            throw "Invalid core specification: $part"
        }
    }
    $mask = [UInt64]0
    for ($i = 0; $i -lt $count -and $i -lt 64; $i++) {
        if ($bits[$i]) { $mask = $mask -bor ([UInt64]1 -shl $i) }
    }
    return $mask
}

function Format-Affinity($value) {
    if ($null -eq $value) { return 'n/a' }
    try { return '0x' + $value.ToInt64().ToString('X') } catch { return 'n/a' }
}

$count = [Environment]::ProcessorCount
$procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { throw "No process found with name: $ProcessName" }

$mask = $null
if ($Apply) {
    if ([string]::IsNullOrWhiteSpace($Cores)) { throw '-Apply requires -Cores (for example "0-7,12").' }
    $mask = Parse-Cores $Cores $count
    Write-Host "Applying mask 0x$($mask.ToString('X')) to $ProcessName" -ForegroundColor Yellow
} else {
    Write-Host "READ-ONLY - use -Apply to change affinity." -ForegroundColor Cyan
}

foreach ($p in $procs) {
    Write-Host "`n$($p.ProcessName) PID $($p.Id)" -ForegroundColor Cyan
    Write-Host "  process affinity : $(Format-Affinity $p.ProcessorAffinity)"
    Write-Host "  threads          : $($p.Threads.Count)"

    if ($Apply -and -not $Threads) {
        $p.ProcessorAffinity = [IntPtr][long]$mask
        Write-Host "  new process mask : $(Format-Affinity $p.ProcessorAffinity)" -ForegroundColor Green
    }

    $sample = @($p.Threads | Select-Object -First 12)
    foreach ($t in $sample) {
        if ($Apply -and $Threads) {
            $t.ProcessorAffinity = [IntPtr][long]$mask
            Write-Host "  thread $($t.Id): mask set to 0x$($mask.ToString('X'))" -ForegroundColor Green
        } else {
            Write-Host "  thread $($t.Id): (affinity is write-only via .NET; use ETW for per-thread view)"
        }
    }
    if ($p.Threads.Count -gt $sample.Count) {
        Write-Host "  ... $(($p.Threads.Count - $sample.Count)) more threads not listed"
    }
}
