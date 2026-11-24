param(
    [string]$OpusRoot = 'C:\Program Files\Epic Games\UE_5.7\Engine\Source\ThirdParty\libOpus\opus-1.3.1-12',
    [string]$OpusLibrary = '',
    [string]$CompilerRoot = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.50.35717',
    [string]$SdkRoot = 'C:\Program Files (x86)\Windows Kits\10',
    [string]$SdkVersion = '10.0.26100.0'
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OpusLibrary)) { $OpusLibrary = Join-Path $OpusRoot 'bin\Win64\Release\opus.lib' }
$opusLib = [IO.Path]::GetFullPath($OpusLibrary)
if (!(Test-Path -LiteralPath $opusLib) -or !(Test-Path -LiteralPath (Join-Path $OpusRoot 'include\opus.h'))) { throw 'Supply -OpusRoot with libopus headers and -OpusLibrary with a compatible x64 static library built using /MT.' }
$env:INCLUDE = (Join-Path $opusRoot 'include') + ';' + (Join-Path $compilerRoot 'include') + ';' + (Join-Path $sdkRoot "Include\$sdkVersion\ucrt") + ';' + (Join-Path $sdkRoot "Include\$sdkVersion\um") + ';' + (Join-Path $sdkRoot "Include\$sdkVersion\shared")
$env:LIB = (Join-Path $compilerRoot 'lib\x64') + ';' + (Join-Path $sdkRoot "Lib\$sdkVersion\ucrt\x64") + ';' + (Join-Path $sdkRoot "Lib\$sdkVersion\um\x64")
$env:PATH = (Join-Path $compilerRoot 'bin\Hostx64\x64') + ';' + $env:PATH
& (Join-Path $compilerRoot 'bin\Hostx64\x64\cl.exe') /nologo /O2 /EHsc /W4 /std:c++17 /MT /D_CRT_SECURE_NO_WARNINGS (Join-Path $PSScriptRoot 'OpusPreview.cpp') ('/Fo'+(Join-Path $PSScriptRoot 'OpusPreview.obj')) ('/Fe'+(Join-Path $PSScriptRoot 'opus-preview.exe')) $opusLib /link /NODEFAULTLIB:MSVCRT /IGNORE:4217
if ($LASTEXITCODE -ne 0) { throw 'Opus preview helper build failed.' }
