param(
    [ValidateSet('leonida','xcl','victory-fork','all')]
    [string]$Component = 'all',
    [string]$GameDir = '',
    [string]$WorkDir = '',
    [switch]$NoRelease
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'msvc.ps1')

function Test-Tool([string]$name) { return $null -ne (Get-Command $name -ErrorAction SilentlyContinue) }

function Build-Leonida {
    $dir = Join-Path $root 'src\leonida'
    if (-not (Test-Tool 'python')) { throw 'Python 3.12+ is required for leonida.' }
    Push-Location $dir
    try {
        $args = @('-I', 'make_game.py')
        if ([string]::IsNullOrWhiteSpace($GameDir)) {
            $args += '--help'
            Write-Host 'Sem -GameDir: apenas validando a ferramenta.' -ForegroundColor Yellow
        } else {
            if (-not (Test-Path $GameDir)) { throw "Game directory not found: $GameDir" }
            $args += @('--game', $GameDir, '--stages', 'check,shaders,runtime')
            if (-not [string]::IsNullOrWhiteSpace($WorkDir)) { $args += @('--work', $WorkDir) }
        }
        & python @args
    } finally { Pop-Location }
}

function Build-Xcl {
    $dir = Join-Path $root 'src\xcl'
    Initialize-Msvc
    if (-not (Test-Tool 'cargo')) { throw 'Rust stable-x86_64-pc-windows-msvc is required for xcl: https://rustup.rs' }
    Push-Location $dir
    try {
        if ($NoRelease) { & cargo build --workspace } else { & cargo build --release --workspace }
    } finally { Pop-Location }
}

function Build-Victory {
    $dir = Join-Path $root 'src\victory-fork'
    Initialize-Msvc
    if (-not (Test-Tool 'cargo')) { throw 'Rust (rust-toolchain.toml) is required for victory-fork: https://rustup.rs' }
    Push-Location $dir
    try {
        if ($NoRelease) { & cargo build -p VanguardScarletX } else { & cargo build --release -p VanguardScarletX }
    } finally { Pop-Location }
}

Write-Host "XRMLX-Fusion build: $Component" -ForegroundColor Cyan

switch ($Component) {
    'leonida'      { Build-Leonida }
    'xcl'          { Build-Xcl }
    'victory-fork' { Build-Victory }
    'all'          {
        Build-Xcl
        Build-Victory
        Build-Leonida
    }
}

Write-Host "Build command finished for $Component (exit $LASTEXITCODE)." -ForegroundColor Cyan
