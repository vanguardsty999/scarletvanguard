function Initialize-Msvc {
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'Não encontrei vswhere.exe. Instale o Visual Studio Build Tools com o workload "Desktop development with C++".'
    }
    $install = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1).Trim()
    if ([string]::IsNullOrWhiteSpace($install)) {
        throw 'Nenhuma instalação do Visual Studio com as ferramentas C++ x64 foi encontrada.'
    }
    $bat = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
    if (-not (Test-Path $bat)) { throw "vcvars64.bat não encontrado em: $bat" }

    $dump = & cmd.exe /c "`"$bat`" >nul 2>&1 && set"
    foreach ($line in $dump) {
        if ($line -match '^([^=]+)=(.*)$') {
            Set-Item -Path "Env:$($Matches[1])" -Value $Matches[2] -ErrorAction SilentlyContinue
        }
    }

    $cargoBin = Join-Path $env:USERPROFILE '.cargo\bin'
    if (Test-Path $cargoBin) { $env:PATH = "$cargoBin;$env:PATH" }
}
