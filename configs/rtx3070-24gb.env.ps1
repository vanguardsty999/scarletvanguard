# Hardware profile: RTX 3070 (8 GB VRAM) + 24 GB RAM + i7-10700 (8c/16t).
# Only variables that were found in the checked-out source are set here.
# Dot-source this file, then call Set-ProfileEnv or Set-TranslateEnv.
# ASCII only: PowerShell 5.1 without BOM mangles accented characters.

$Profile = @{
    Name = 'rtx3070-24gb'
    Paths = @{
        XclDataRoot     = 'F:\XCL-data'
        XclDxcDir       = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
        VictoryCacheDir = 'F:\victory-cache'
        VictoryDataDir  = 'F:\victory-data'
    }
    Affinity = @{
        JobRenderMask = '0-11'   # 0x0FFF - 12 logical CPUs
        DriverMask    = '12-15'  # 0xF000 - 4 logical CPUs for PSO/driver work
    }
}

# Runtime switches. VICTORY_HOST_WAVE / VICTORY_HOST_MAX_WAVE are NOT set here:
# the source reads them only in `victory translate`, never in `victory run`.
function Set-ProfileEnv {
    param(
        [ValidateSet('safe','speed')]
        [string]$Preset = 'safe',
        [switch]$Trace
    )

    # XCL: source references in xcl/crates/xcl/src/{config.rs,gpu/recomp/*,hle/dstorage/parallel.rs}
    $env:XCL_ADAPTER = 'nvidia'
    $env:XCL_RAYTRACING = 'off'
    $env:XCL_DATA_ROOT = $Profile.Paths.XclDataRoot
    # DXC 1.8 is in the installed Windows SDK. XCL recommends it: DXC 1.7
    # produced 2-2.7x larger DXIL and 1.4-2.2x slower PSO builds on NVIDIA.
    if (Test-Path -LiteralPath $Profile.Paths.XclDxcDir) {
        $env:XCL_DXC_DIR = $Profile.Paths.XclDxcDir
    } else {
        Remove-Item 'Env:XCL_DXC_DIR' -ErrorAction SilentlyContinue
    }
    $env:XCL_HUGE_PSO_CONCURRENCY = '1'
    $env:XCL_DXC_THREADS = '6'
    # Two driver pipeline compiles balance throughput with stability: the XCL
    # source documents that several large NVIDIA compilations can consume tens
    # of GB in the driver.
    $env:XCL_PIPELINE_THREADS = '2'
    $env:XCL_DSTORAGE_PARALLEL_DECOMPRESS = '1'
    $env:XCL_DSTORAGE_MEMORY_BUDGET_MIB = if ($Preset -eq 'safe') { '32' } else { '64' }
    $env:XCL_DSTORAGE_WORKERS = if ($Preset -eq 'safe') { '2' } else { '4' }

    # src/victory: source references in src/victory/crates/victory-gpu/src/*
    $env:VICTORY_ADAPTER = 'nvidia'
    $env:VICTORY_CACHE_DIR = $Profile.Paths.VictoryCacheDir
    $env:VICTORY_DATA_DIR = $Profile.Paths.VictoryDataDir
    # The console reports 3840x2160 by default in src/victory. Tell the title
    # it is on a 720p display so it picks smaller render targets.
    $env:VICTORY_DISPLAY_MODE = '1280x720'
    $env:VICTORY_PIPELINE_WAIT_MS = '24'
    $env:VICTORY_D3D12_BACKGROUND = '0'
    $env:VICTORY_SHADER_OPT = '1'
    $env:VICTORY_INPLACE = '0'
    $env:VICTORY_BVH_BUILDER = '0'
    $env:VICTORY_CARD_BUFFERS = '1'

    if ($Preset -eq 'safe') {
        # Documented in src/victory/README.md as the lower-memory path.
        $env:VICTORY_WIDE_BACK = '0'
        $env:VICTORY_LATE_WAIT = '0'
        $env:VICTORY_CARD_BUFFERS_MIN_MB = '1'
        $env:VICTORY_CARD_BUFFERS_MB = '256'
        $env:VICTORY_TEXTURE_BUDGET_MB = '3072'
    } else {
        # Documented combo: LATE_WAIT=3 WIDE_BACK=1 CARD_BUFFERS_MIN_MB=1 CARD_BUFFERS_MB=1024.
        $env:VICTORY_WIDE_BACK = '1'
        $env:VICTORY_LATE_WAIT = '3'
        $env:VICTORY_CARD_BUFFERS_MIN_MB = '1'
        $env:VICTORY_CARD_BUFFERS_MB = '1024'
        $env:VICTORY_TEXTURE_BUDGET_MB = '4096'
    }
    $env:VICTORY_RELAY = '2'

    if ($Trace) {
        $env:XCL_PSO_TRACE = '1'
        $env:XCL_DRED = '1'
        $env:XCL_FRAME_DUMP = '2'
        $env:VICTORY_GPU_TIMING = '1'
        $env:VICTORY_D3D12_DRED = '1'
    } else {
        foreach ($name in @('XCL_PSO_TRACE','XCL_DRED','XCL_FRAME_DUMP','VICTORY_GPU_TIMING','VICTORY_D3D12_DRED')) {
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
    }
}

# Shader translation only (victory translate / xcl shaders --precompile).
# These change how GPU code is translated for a 32-lane NVIDIA wave.
function Set-TranslateEnv {
    $env:VICTORY_HOST_WAVE = '32'
    $env:VICTORY_HOST_MAX_WAVE = '32'
}

function Show-ProfilePlan {
    Write-Host "Profile: $($Profile.Name)" -ForegroundColor Cyan
    Write-Host "  CPU affinity for job/render threads: $($Profile.Affinity.JobRenderMask)" -ForegroundColor Yellow
    Write-Host "  CPU affinity left for driver/PSO:    $($Profile.Affinity.DriverMask)" -ForegroundColor Yellow
    Write-Host "  XCL data root:     $($Profile.Paths.XclDataRoot)" -ForegroundColor Yellow
    Write-Host "  DXC:               $($Profile.Paths.XclDxcDir)" -ForegroundColor Yellow
    Write-Host "  Display reported:  1280x720 (victory default is 3840x2160)" -ForegroundColor Yellow
    Write-Host "  Victory cache:     $($Profile.Paths.VictoryCacheDir)" -ForegroundColor Yellow
    Write-Host "  Victory data:      $($Profile.Paths.VictoryDataDir)" -ForegroundColor Yellow
    Write-Host '  VRAM is 8 GB (WMI reports 4 GB because AdapterRAM is 32-bit).' -ForegroundColor Yellow
    Write-Host '  Start at 1280x720; raise only after p95 frame time is measured.' -ForegroundColor Yellow
    Write-Host '  Pagefile: raise C:\pagefile.sys from 17 GB to 48-64 GB (needs admin/reboot).' -ForegroundColor Yellow
}
