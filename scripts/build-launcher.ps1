# Build the Scarlet Vanguard DX12 port launcher.
# ASCII only: PowerShell 5.1 without BOM mangles accented characters.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $repo 'scripts\launcher\ScarletVanguardLauncher.cs'
$out = Join-Path $repo 'ScarletVanguard-Launcher.exe'
$brand = Join-Path $repo 'assets\branding\scarlet-vanguard-banner.png'

if (-not (Test-Path -LiteralPath $csc)) { throw "csc.exe not found: $csc" }
if (-not (Test-Path -LiteralPath $source)) { throw "source not found: $source" }
New-Item -ItemType Directory -Path (Split-Path -Parent $out) -Force | Out-Null

$refs = @(
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Management.dll',
    '/r:System.Windows.Forms.dll'
)
& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:$out @refs $source
if ($LASTEXITCODE -ne 0) { throw "csc.exe exited $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $brand)) { throw "brand asset not found: $brand" }
# Keep the legacy bin entry point in sync for existing shortcuts.
New-Item -ItemType Directory -Path (Join-Path $repo 'bin') -Force | Out-Null
Copy-Item -LiteralPath $out -Destination (Join-Path $repo 'bin\ScarletVanguard-Launcher.exe') -Force

$hash = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash
Write-Output "LAUNCHER=$out"
Write-Output "BYTES=$((Get-Item -LiteralPath $out).Length)"
Write-Output "SHA256=$hash"
