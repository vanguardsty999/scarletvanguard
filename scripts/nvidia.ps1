param(
    [ValidateSet('xcl','victory-fork','victory-nvidia','env')]
    [string]$Component = 'env',
    [string]$GamePath = '',
    [string]$Adapter = 'nvidia',
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'msvc.ps1')

Initialize-Msvc

# XCL: escolhe o adaptador por substring do nome (ex.: "nvidia", "rtx 4070") ou índice.
$env:XCL_ADAPTER = $Adapter
# victory-fork: o fork agora entende VICTORY_ADAPTER; 32 lanes é o caso NVIDIA.
$env:VICTORY_ADAPTER = $Adapter
# HOST_WAVE/HOST_MAX_WAVE valem apenas para `victory translate`, nao para `victory run`.
$env:VICTORY_HOST_WAVE = '32'
$env:VICTORY_HOST_MAX_WAVE = '32'

Write-Host "Perfil NVIDIA ativado (adaptador: $Adapter)" -ForegroundColor Cyan
Write-Host "  XCL_ADAPTER            = $env:XCL_ADAPTER"
Write-Host "  VICTORY_ADAPTER        = $env:VICTORY_ADAPTER"
Write-Host "  VICTORY_HOST_WAVE      = $env:VICTORY_HOST_WAVE (translate only)"
Write-Host "  VICTORY_HOST_MAX_WAVE  = $env:VICTORY_HOST_MAX_WAVE (translate only)"

if ($Build) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Component $Component
}

if ($Component -eq 'env' -or [string]::IsNullOrWhiteSpace($GamePath)) {
    Write-Host 'Use -Component xcl|victory-fork e -GamePath para executar o jogo. victory-nvidia remains an alias.' -ForegroundColor Yellow
    return
}

if (-not (Test-Path $GamePath)) { throw "Caminho do jogo não encontrado: $GamePath" }

switch ($Component) {
    'xcl' {
        $exe = Join-Path $root 'xcl\target\release\xcl.exe'
        if (-not (Test-Path $exe)) { throw "Build ausente: $exe" }
        & $exe run $GamePath
    }
    'victory-fork' {
        # `play` applies the current fork's renderer defaults, F1/F3 UI and
        # pipeline-book settings; raw `run --execute` intentionally does not.
        & (Join-Path $PSScriptRoot 'victory-nvidia.ps1') -GameExe $GamePath
    }
    'victory-nvidia' {
        & (Join-Path $PSScriptRoot 'victory-nvidia.ps1') -GameExe $GamePath
    }
}
