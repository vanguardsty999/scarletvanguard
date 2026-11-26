param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir,
    [int]$MaxEntries = 500000
)

$ErrorActionPreference = 'Stop'
$literal = $GameDir
if (-not (Test-Path -LiteralPath $literal)) { throw "Directory not found: $literal" }

Write-Host "READ-ONLY PROBE - no file will be opened, copied or modified." -ForegroundColor Cyan
Write-Host "Target: $literal"

$all = @(Get-ChildItem -LiteralPath $literal -Recurse -File -Force -ErrorAction SilentlyContinue | Select-Object -First $MaxEntries)
$totalBytes = ($all | Measure-Object -Property Length -Sum).Sum

Write-Host "`n[summary]"
Write-Host "  files listed : $($all.Count)"
Write-Host "  total size   : $([math]::Round($totalBytes / 1GB, 2)) GB"

Write-Host "`n[largest files]"
$all | Sort-Object Length -Descending | Select-Object -First 15 @{
    n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) }
}, @{ n = 'Path'; e = { $_.FullName.Substring($literal.Length).TrimStart('\') } } | Format-Table -AutoSize

Write-Host "`n[top extensions]"
$all | Group-Object { if ($_.Extension) { $_.Extension.ToLowerInvariant() } else { '(no extension)' } } |
    Sort-Object Count -Descending | Select-Object -First 15 Count, Name | Format-Table -AutoSize

Write-Host "`n[executables at root]"
$exe = @(Get-ChildItem -LiteralPath $literal -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Extension -ieq '.exe' })
if ($exe.Count -eq 0) { Write-Host '  none yet (download still running?)' -ForegroundColor Yellow }
else { $exe | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }, LastWriteTime | Format-Table -AutoSize }

Write-Host "`n[partial download markers]"
$partial = @($all | Where-Object { $_.Name -match '\.(part|partial|!ut|crdownload|tmp)$' })
if ($partial.Count -eq 0) { Write-Host '  none' -ForegroundColor Green }
else { $partial | Select-Object -First 20 Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize }

Write-Host "`nProbe finished. The game directory was not changed." -ForegroundColor Green
