$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$upstream = Join-Path (Split-Path -Parent $root) 'XRMLX'

if (-not (Test-Path $upstream)) { throw "Origem não encontrada: $upstream" }

foreach ($name in @('leonida', 'xcl', 'victory')) {
    $sourceName = if ($name -eq 'victory') { 'src/victory' } else { $name }
    $src = Join-Path $upstream $sourceName
    $dst = Join-Path $root (Join-Path 'src' $name)
    Write-Host "`n== $name ==" -ForegroundColor Cyan
    if (-not (Test-Path $src)) { Write-Host "  pulado (origem ausente)" -ForegroundColor Yellow; continue }
    & robocopy $src $dst /E /XD .git /XF .git /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "robocopy falhou em $name (código $LASTEXITCODE)" }
    Write-Host "  sincronizado (robocopy $LASTEXITCODE)"
}

Write-Host "`nConcluído. Rode .\scripts\validate.ps1 para conferir a árvore." -ForegroundColor Yellow
