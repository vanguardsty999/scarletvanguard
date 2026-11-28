# Documentação de preparação

| Documento | Objetivo |
|---|---|
| [`PLATFORM_ABSTRACTION.md`](PLATFORM_ABSTRACTION.md) | Como trocar XDK por Win32 aos poucos: abstração nos módulos HLE ou shim estilo Proton quando não há fonte. |
| [`PERF_RUNBOOK.md`](PERF_RUNBOOK.md) | Medir antes de otimizar: PIX/VTune/ETW, PSO em runtime e afinidade de threads. |
| [`HARDWARE_PROFILE_RTX3070.md`](HARDWARE_PROFILE_RTX3070.md) | Perfil safe/speed para RTX 3070 + 24 GB, limites de VRAM/RAM/pagefile e o launcher Scarlet Vanguard. |
| [`XBOX_PARTICULARITIES.md`](XBOX_PARTICULARITIES.md) | Diferenças Scarlett → Windows em shader/PSO, wave64→wave32, render distance, cache e memory swap, separando fonte verificada, documentação externa e hipótese. |
| [`XBOX_STREAMING_AND_DX12.md`](XBOX_STREAMING_AND_DX12.md) | Diagnóstico do streaming local e como DirectStorage/tiled resources se traduzem para o perfil D3D12 da RTX 3070. |
| [`STARTUP_CRASH_DIAGNOSIS.md`](STARTUP_CRASH_DIAGNOSIS.md) | Diagnóstico do `int3` em `System Init` (frame 0) que bloqueia XCL e victory antes de qualquer medição de FPS. |

Scripts correspondentes (todos com modo padrão **somente leitura / sem executar jogo**):

```powershell
.\scripts\probe-game.ps1 -GameDir "N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK"
.\scripts\pso-precompile.ps1 -GameDir "N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK"
.\scripts\measure.ps1 -Component xcl -GamePath "...\game_scarlett_master.exe" -Tool all
.\scripts\measure.ps1 -Component victory-fork -GamePath "...\game_scarlett_master.exe" -Tool etw
.\scripts\affinity.ps1 -ProcessName victory -Cores 0-7
.\scripts\optimize-rtx3070.ps1 -Preset safe -Component none
```

- `probe-game.ps1` apenas lista metadados; não abre, copia ou escreve no jogo.
- `pso-precompile.ps1` gera comandos em `precompile-commands.txt`; `-Execute` é necessário para rodar.
- `measure.ps1` cria `measurements\<timestamp>\commands.txt` e aplica variáveis de trace apenas no próprio processo; `-Launch` é necessário para iniciar o jogo.
- `affinity.ps1` mostra afinidade atual; `-Apply` (e opcionalmente `-Threads`) é necessário para alterar.
- `optimize-rtx3070.ps1` aplica o perfil verificado; `-Launch` é necessário para iniciar o jogo.
- Arquivos do jogo, caches, logs e executáveis compilados não pertencem ao repositório; todos são ignorados pelo Git.
