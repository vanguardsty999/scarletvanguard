# Perfil de hardware — RTX 3070 / 24 GB RAM

Este documento descreve o perfil `configs/rtx3070-24gb.env.ps1` e o launcher `ScarletVanguard.exe`.

## 1. Hardware detectado

| Item | Valor medido |
|---|---|
| CPU | Intel Core i7-10700 @ 2,90 GHz — 8 núcleos / 16 threads |
| RAM | 23,92 GB totais, 8,95 GB livres no momento da medição |
| GPU | NVIDIA GeForce RTX 3070 — **8 GB de VRAM reais** |
| Driver NVIDIA | 32.0.16.2114 |
| Pagefile | `C:\pagefile.sys`, 17.408 MB (~17 GB) |
| Discos | C: 170 GB, F: 707 GB, N: 216 GB livres |

O WMI reporta `AdapterRAM = 4 GB` por causa do campo de 32 bits; a RTX 3070 tem 8 GB. Não use esse número para dimensionar o perfil.

## 2. Limite honesto de FPS

**60 fps+ não é alcançável com o estado atual dos upstreams.** Não é uma questão de ajustar variáveis:

- `src/victory/README.md` registra **2–4 fps** no prólogo (420–450 ms/frame; 240–350 ms com os switches novos), com iluminação em sombra ainda ausente.
- XCL registra ~28–30 fps com watchdog em cenários próprios, sem sessão de jogo reproduzida localmente.
- Nenhum desses números foi medido nesta máquina. O `.exe` do jogo ainda não terminou de baixar.

O perfil existe para **medir e reduzir frame time**, não para prometer 60 fps. A meta de curto prazo é estabelecer uma linha de base (p50/p95/p99) e eliminar compilações de PSO durante o gameplay. Veja `PERF_RUNBOOK.md`.

## 3. Presets

### `safe` (padrão)

- `XCL_ADAPTER=nvidia`, `XCL_RAYTRACING=off`
- `XCL_DATA_ROOT=F:\XCL-data` — tira cache do disco do sistema
- `XCL_HUGE_PSO_CONCURRENCY=1`, `XCL_DXC_THREADS=6`, `XCL_PIPELINE_THREADS=4`
- `XCL_DSTORAGE_PARALLEL_DECOMPRESS=1`, `XCL_DSTORAGE_WORKERS=2`, `XCL_DSTORAGE_MEMORY_BUDGET_MIB=32`
- `VICTORY_ADAPTER=nvidia`, `VICTORY_CACHE_DIR=F:\victory-cache`, `VICTORY_DATA_DIR=F:\victory-data`
- `VICTORY_PIPELINE_WAIT_MS=24`, `VICTORY_D3D12_BACKGROUND=0`, `VICTORY_SHADER_OPT=1`
- `VICTORY_INPLACE=0`, `VICTORY_BVH_BUILDER=0` (ray tracing incompleto no fork)
- `VICTORY_WIDE_BACK=0`, `VICTORY_LATE_WAIT=0`, `VICTORY_RELAY=2`
- `VICTORY_CARD_BUFFERS_MIN_MB=1`, `VICTORY_CARD_BUFFERS_MB=256`
- `VICTORY_TEXTURE_BUDGET_MB=3072` — teto para cópias de textura na RAM

### `speed`

Só depois de medir o `safe`. Troca memória por paralelismo:

- `XCL_DSTORAGE_WORKERS=4`, `XCL_DSTORAGE_MEMORY_BUDGET_MIB=64`
- `VICTORY_WIDE_BACK=1`, `VICTORY_LATE_WAIT=3`
- `VICTORY_CARD_BUFFERS_MB=1024`, `VICTORY_TEXTURE_BUDGET_MB=4096`
- Combinação documentada no README do src/victory; **não medida nesta máquina**.

### `-Trace`

Adiciona `XCL_PSO_TRACE=1`, `XCL_DRED=1`, `XCL_FRAME_DUMP=2`, `VICTORY_GPU_TIMING=1`, `VICTORY_D3D12_DRED=1`. Tem overhead: use só durante captura.

## 4. Variáveis que só valem na tradução

`VICTORY_HOST_WAVE=32` e `VICTORY_HOST_MAX_WAVE=32` são lidas em `src/victory/apps/victory/src/translate.rs` e `main.rs`, **apenas no subcomando `translate`**. Definir isso antes de `victory run` não muda nada em runtime.

Elas são aplicadas por `Set-TranslateEnv` (chamada por `pso-precompile.ps1`) para gerar DXIL com waves de 32 lanes, adequado à NVIDIA. Não devem ser confundidas com otimização de runtime.

`--precompile --pipelines` do `xcl shaders` existe (`shaders.rs:894`), embora não apareça no `--help`.

## 5. Afinidade

`i7-10700` tem 16 threads lógicos. O perfil separa:

- jobs/render: cores `0-11` (`0x0FFF`)
- driver e compilação de PSO: cores `12-15` (`0xF000`)

Aplicar: `.\scripts\optimize-rtx3070.ps1 -Preset safe -ApplyAffinity -Launch ...`.
A leitura da afinidade por thread via .NET é somente-gravação; use ETW para ver a distribuição real.

## 6. Pagefile

Com 23,92 GB de RAM e um título que chegou a ocupar ~24 GB no upstream, 17 GB de pagefile é insuficiente. Recomenda-se **48–64 GB** em `C:` (ou outro disco rápido). Isso exige direitos de administrador e reinício — não foi alterado aqui.

## 7. Uso

```powershell
# Somente mostra o plano e as variáveis; não lança nada
.\scripts\optimize-rtx3070.ps1 -Preset safe -Component none

# Quando o .exe existir e você autorizar
.\scripts\optimize-rtx3070.ps1 -Preset safe -Component src/victory `
    -GamePath "N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK\game_scarlett_master.exe" `
    -ApplyAffinity -Launch

# Executavel unico: abre o titulo ja com o perfil aplicado
.\bin\ScarletVanguard.exe -Probe      # so mostra o plano
.\bin\ScarletVanguard.exe             # lanca src/victory com safe + afinidade
.\bin\ScarletVanguard.exe -Preset speed -Trace   # variante agressiva + diagnosticos
```

`bin\ScarletVanguard.exe` é um **bundle**: stub nativo + o PE validado do título anexado como overlay + um rodapé `SVBUNDLE` com o tamanho do payload. Ao abrir, ele extrai o título uma vez para `%LOCALAPPDATA%\ScarletVanguard\title\` (SHA-256 idêntico ao original), aplica `Set-ProfileEnv` e chama o loader. Nada é escrito no diretório do download.

O bundle já vem com: `XCL_ADAPTER/VICTORY_ADAPTER=nvidia`, ray tracing desligado, caches em `F:`, limites de threads de PSO/DXC/DirectStorage, `VICTORY_SHADER_OPT=1`, `RELAY=2`, `WIDE_BACK=0`, `LATE_WAIT=0`, tetos de memória e afinidade `0-11`. `-Preset speed` liga os switches agressivos não medidos; `-NoAffinity` desliga a afinidade.

Para recriar o bundle (o `.exe` é ignorado pelo `*.exe` do `.gitignore`):

```powershell
.\scripts\build-bundle.ps1
# opcional: -TitleExe <outro PE> -Out <caminho>
```

Resolução inicial: **1280x720**. Só aumente depois de medir p95.

## 8. Validação de `game_scarlett_master.exe`

O executável foi localizado na raiz de `XRMLX-Fusion` (cópia feita pelo usuário) e analisado por `xcl inspect` e `victory inspect` — somente leitura:

| Propriedade | Valor |
|---|---|
| Tamanho | 120.181.760 bytes (114,61 MB), criado em 26/11/2026 05:25 |
| Formato | PE x64, subsystem GUI, 10 seções, relocações e TLS (5.248 B) |
| ImageBase / entry | `0x140000000` / `+0x56a1ec8` |
| Exceções | 299.257 entradas `.pdata` |
| PDB | `X:/americas/src/sub_story/game/.../Gaming.Xbox.Scarlett.x64/Build/master/game_scarlett_master.pdb` |
| SHA-256 | `E95020B2FA4A721DB3DF59FDECF75621730AC559D8835252F6C3B658D5A08993` |
| blake3 | `b99f876b74d4a77f03af5f878223b27bd647b5c35a79d5decd9f716504239e93` |
| Imports | 587 funções de 68 DLLs |

`xcl inspect` aponta **73 imports não implementados (stubs)**, concentrados em:

| DLL | Stubs | Impacto provável |
|---|---|---|
| `libHttpClient.GDK.dll` | 38 | rede — pode ficar como stub offline |
| `Bink2Scarlett.dll` | 23 | vídeos/cutscenes — afeta abertura |
| `XboxBvhBuilder.dll` | 7 | BVH de ray tracing (incompleto no fork) |
| `amd_fidelityfx_loader_xbox.dll` | 4 | FFX/upscaling |
| `xmem.dll` | 1 | memória do console |

`victory census <exe> --missing` chega ao mesmo grupo: 72 imports ausentes no victory (38 + 23 + 7 + 4), com `xmem.dll` já coberto. Relatórios completos em `measurements/inspect-game/`. Essa contagem é a priorização concreta para a camada de abstração descrita em `PLATFORM_ABSTRACTION.md` — não intuição.

## 9. O que NÃO foi feito

- Nenhum arquivo do diretório `N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK` foi modificado.
- Foi feita uma **cópia byte-a-byte** do título em `bin\ScarletVanguard-Title.exe` (SHA-256 idêntico ao original na raiz); o original não foi alterado. Renomear um PE não muda o que o loader exige — a cópia existe para dar ao projeto um executável com o nome dele, não para alterar compatibilidade.
- O executável novo criado é `bin\ScarletVanguard.exe`: contém o título incorporado e os switches de otimização já aplicados por padrão. Ele não é um PE recompilado do jogo — é o PE do jogo mais um stub que configura e chama o loader.
- Nenhuma otimização foi injetada no código do jogo: XCL/src/victory continuam sendo quem implementa HLE, tradução de shaders e agendamento.
- Nenhuma sessão de jogo, captura PIX/VTune/ETW ou pré-compilação foi executada.
- Nenhum commit foi feito nesta rodada.
