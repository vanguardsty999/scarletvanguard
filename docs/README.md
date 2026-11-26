# Documentação de preparação

| Documento | Objetivo |
|---|---|
| [`PLATFORM_ABSTRACTION.md`](PLATFORM_ABSTRACTION.md) | Como trocar XDK por Win32 aos poucos: abstração nos módulos HLE ou shim estilo Proton quando não há fonte. |
| [`PERF_RUNBOOK.md`](PERF_RUNBOOK.md) | Medir antes de otimizar: PIX/VTune/ETW, PSO em runtime e afinidade de threads. |

Scripts correspondentes (todos com modo padrão **somente leitura / sem executar jogo**):

```powershell
.\scripts\probe-game.ps1 -GameDir "N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK"
.\scripts\pso-precompile.ps1 -GameDir "N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK"
.\scripts\measure.ps1 -Component xcl -GamePath "...\game_scarlett_master.exe" -Tool all
.\scripts\measure.ps1 -Component victory-fork -GamePath "...\game_scarlett_master.exe" -Tool etw
.\scripts\affinity.ps1 -ProcessName victory -Cores 0-7
```

- `probe-game.ps1` apenas lista metadados; não abre, copia ou escreve no jogo.
- `pso-precompile.ps1` gera comandos em `precompile-commands.txt`; `-Execute` é necessário para rodar.
- `measure.ps1` cria `measurements\<timestamp>\commands.txt` e aplica variáveis de trace apenas no próprio processo; `-Launch` é necessário para iniciar o jogo.
- `affinity.ps1` mostra afinidade atual; `-Apply` (e opcionalmente `-Threads`) é necessário para alterar.
