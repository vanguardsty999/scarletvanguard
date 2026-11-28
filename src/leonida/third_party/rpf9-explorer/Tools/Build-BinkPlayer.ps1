$ErrorActionPreference = 'Stop'
$compilerRoot = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.50.35717'
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$sdkVersion = '10.0.26100.0'
$binkLib = 'C:\Program Files\Epic Games\UE_5.7\Engine\Plugins\Media\BinkMedia\Source\BinkMediaPlayerSDK\lib\BinkUnrealWin64.lib'
if (!(Test-Path -LiteralPath $binkLib)) { throw 'The installed Unreal Bink SDK is required.' }
$env:INCLUDE = (Join-Path $compilerRoot 'include') + ';' + (Join-Path $sdkRoot "Include\$sdkVersion\ucrt") + ';' + (Join-Path $sdkRoot "Include\$sdkVersion\um") + ';' + (Join-Path $sdkRoot "Include\$sdkVersion\shared")
$env:LIB = (Join-Path $compilerRoot 'lib\x64') + ';' + (Join-Path $sdkRoot "Lib\$sdkVersion\ucrt\x64") + ';' + (Join-Path $sdkRoot "Lib\$sdkVersion\um\x64")
$env:PATH = (Join-Path $compilerRoot 'bin\Hostx64\x64') + ';' + $env:PATH
& (Join-Path $compilerRoot 'bin\Hostx64\x64\cl.exe') /nologo /O2 /EHsc /std:c++17 /MT /D_CRT_SECURE_NO_WARNINGS (Join-Path $PSScriptRoot 'BinkPlayer.cpp') ('/Fo'+(Join-Path $PSScriptRoot 'BinkPlayer.obj')) ('/Fe'+(Join-Path $PSScriptRoot 'bink-player.exe')) $binkLib winmm.lib user32.lib advapi32.lib ole32.lib
if ($LASTEXITCODE -ne 0) { throw 'Bink playback helper build failed.' }
