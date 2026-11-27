# Particularidades Xbox Scarlett → Windows

Pesquisa feita em 26/11/2026 cruzando **código desta árvore** (XCL e victory-fork) com documentação pública Microsoft/AMD/XCL. O objetivo é separar:

- **[V]** verificado no fonte ou no sistema desta máquina;
- **[D]** documentado externamente, mas não medido aqui;
- **[H]** hipótese específica deste título — **não** deve ser tratada como controle existente.

Nada aqui foi executado dentro do jogo. Não há medição de FPS, PIX, VTune ou ETW nesta rodada.

---

## 1. Resumo executivo

| Área | Ajuste real | Evidência |
|---|---|---|
| Shader / PSO | Manter PSO assíncrono, lazy e preload; limitar compilações "huge"; usar DXC 1.8; aquecer cache antes de jogar | [V] + [D] |
| Wave64 → Wave32 | RTX 3070 tem waves de 32 lanes; a tradução divide cada wave64 do console em duas metades e troca máscaras/valores. Isso é correto, mas mais caro | [D] + [V] |
| Render distance | **Não existe variável de "distância de renderização"** em XCL/victory-fork. Culling/LOD é lógica do jogo em shaders de mesh/amplification | [V] + [D] |
| Resolução | victory-fork reporta **3840x2160** ao título por padrão; agora o perfil força `VICTORY_DISPLAY_MODE=1280x720` | [V] |
| Cache | XCL e victory-fork têm caches próprios em disco; chaves de cache incluem flags de tradução. Não mexer debug/switches no runtime | [V] |
| Memória / swap | Console tem UMA de 16 GB; RTX 3070 tem 8 GB dedicados. Passar do orçamento faz o Windows mover heaps para RAM e pode congelar o processo | [V] + [D] |
| Pagefile | 17 GB atuais são insuficientes para um título que já chegou a ~24 GB no upstream; recomendado 48–64 GB (admin + reboot) | [V] |

---

## 2. Shader e PSO

### 2.1 XCL — controles verificados no fonte

| Variável | Padrão | O que faz | Onde |
|---|---:|---|---|
| `XCL_ASYNC_PSO` | ligado (`!=0`) | Cria PSOs em workers; `0` força criação síncrona | `xcl/crates/xcl/src/hle/d3d12x/pso_async.rs:165-166` |
| `XCL_LAZY_PSO` | ligado | Stand-in imediato; PSO real só no primeiro uso. Comentário do fonte: um título varejista cria ~51.000 PSOs nos primeiros 30 s | `pso_async.rs:207-214` |
| `XCL_PSO_PRELOAD` | ligado | Pré-constrói PSOs usados em execução anterior, em prioridade baixa; 2–4 threads | `pso_async.rs:326-343` |
| `XCL_PSO_WAIT_MS` | ilimitado (`0`/unset) | Tempo máximo que um bind espera por um PSO em construção. O comentário cita watchdog de 30 s do título | `pso_async.rs:562-568` |
| `XCL_HUGE_PSO_BYTES` | 262.144 B | Acima disso, um PSO é "huge" e usa slot exclusivo | `hle/d3d12x/real.rs:3529-3536` |
| `XCL_HUGE_PSO_CONCURRENCY` | 1 | Número de compilações huge simultâneas. O fonte registra que `nvgpucomp64` leva minutos e ~1 GB por programa de 1–2 MB | `real.rs:3539-3542` |
| `XCL_DXC_THREADS` | `available_parallelism()-1`, clamp 1–64 | Threads de compilação DXC e de PSO | `gpu/recomp/dxc.rs:384-395` |
| `XCL_DXIL_CACHE` | `%LOCALAPPDATA%\xcl\dxil` | Cache de DXIL traduzido | `dxc.rs:566-573` |
| `XCL_PSO_DISK_CACHE` | ligado | Cache de PSO do driver, dentro da pasta do DXIL | `real.rs:3916,3934` |
| `XCL_TRANSLATED_CACHE` | ligado | Cache da tradução; `0` desliga | `gpu/recomp/pso.rs:171,235` |
| `XCL_HEAP_WINDOWS` | desligado | Muda a identidade/forma do DXIL (janelas de heap). Não ativar no runtime por curiosidade | `pso.rs:108-114` |
| `XCL_SPLIT_WAVE64`, `XCL_WAVE64_SPIN`, `XCL_PER_LANE`, `XCL_DIRECT_DXIL` | experimentais / teste | Alternam caminhos de tradução; **não** são "otimizadores de runtime" | `pso.rs:772-775`, `emit.rs:4174-4177`, `lower_dxil.rs:392-395`, `gpu/dxil/kernel.rs:42-52` |

Interpretação prática: o gargalo provável não é "shader lento", mas **PSO sendo compilado durante o gameplay**. Por isso o perfil `safe` mantém `ASYNC/Lazy/Preload` ligados e `HUGE_PSO_CONCURRENCY=1`, e o script `pso-precompile.ps1` gera comandos `--precompile --pipelines`.

> `--precompile --pipelines` existe em `xcl/crates/xcl/src/shaders.rs:894-899`, embora não apareça no `--help`.

### 2.2 victory-fork — controles verificados

| Variável | Padrão | O que faz | Onde |
|---|---:|---|---|
| `VICTORY_SHADER_OPT` | unset = todos os passes | Bitmask de passes de limpeza do módulo traduzido; `0` desliga todos. Afeta a chave do cache | `victory-shader/src/emit/mod.rs:171-188` |
| `VICTORY_GROUP_SYNC` | unset/`2` = todas as barreiras esperam | `1` espera só onde seguro; `0` remove esperas e pode travar/errar em NVIDIA | `emit/mod.rs:145-168` |
| `VICTORY_PIPELINE_WAIT_MS` | 500 ms | Orçamento de espera por frame somado entre threads | `victory-gpu/src/pipeline/make.rs:97-107` |
| `VICTORY_LIBRARY_MAP` | ligado | Biblioteca de PSO mapeada em memória em vez de lida por buffers | `pipeline/library.rs:78-81` |
| `VICTORY_LIBRARY_HANDLE` | ligado | Mantém o arquivo aberto após mapear | `library.rs:87-90` |
| `VICTORY_LIBRARY_TRIM` | ligado | Faz trim das páginas da biblioteca | `library.rs:364-370` |
| `VICTORY_LIBRARY_LOCK` | desligado | `1` serializa todas as chamadas da biblioteca | `library.rs:423-427` |
| `VICTORY_HOST_WAVE`, `VICTORY_HOST_MAX_WAVE` | unset | **Somente `victory translate`**, nunca `victory run` | `apps/victory/src/main.rs:156-163`, `translate.rs:163-165` |
| `VICTORY_VIEW_LIMIT` | unset | **Somente `victory translate`**: limita o heap de views | `main.rs:156-158` |
| `VICTORY_SHADER_CENSUS`, `VICTORY_SHADER_SAVE` | unset | Diagnóstico/tradução, não runtime | `translate.rs:485-494`, `pipeline.rs:139-141` |

O perfil usa `VICTORY_SHADER_OPT=1` (somente eliminação de código morto) porque é o passe mais simples e o fonte diz que todos os passes são exatos; isso foi escolhido para **medir**, não porque 1 seja necessariamente mais rápido que unset. Se houver dúvida, unset (todos os passes) é o padrão do autor.

### 2.3 Wave64 em GPU NVIDIA [D]

O XCL documenta que a RTX 3070 (e qualquer GPU com `WaveLaneCountMax < 64`) não aceita compute `[WaveSize(64)]`. A tradução então representa **uma wave64 do console como duas wave32**, trocando máscaras e valores via memória compartilhada. O documento upstream mede overhead de ~1,04–1,16× quando não há troca e de ~4× ou mais quando há trocas em grupos com várias waves. VS/PS não podem ser pareados; o driver escolhe a wave e leituras das lanes 32–63 recebem valores explícitos.

Fonte: `xcl/docs/wave64-on-wave32.md` (também publicado em `github.com/xcl-dev/xcl/blob/main/docs/wave64-on-wave32.md`).

**Ajuste:** nada a "ligar" no runtime. Já é o caminho automático em NVIDIA. `VICTORY_HOST_WAVE=32`/`HOST_MAX_WAVE=32` só afetam a **tradução offline**, e o `Set-TranslateEnv` do perfil faz isso.

### 2.4 Pré-requisitos de plataforma [V] + [D]

Nesta máquina:

| Item | Estado |
|---|---|
| Sistema | Windows 11 Pro, build 26300 |
| `D3D12Core.dll` | `10.0.26100.9818` (sistema; **nenhum** Agility SDK encenado) |
| `xgameruntime.dll` | presente, `10.0.26100.9656` |
| DXC | `1.8.2502.11` em `Windows Kits\10\bin\10.0.26100.0\x64` |
| `XCL_DXC_DIR` | agora apontado para esse diretório pelo perfil |

O README do XCL recomenda DXC ≥ 1.8: DXC 1.7 gerou DXIL 2–2,7× maior e PSOs 1,4–2,2× mais lentos em NVIDIA. Ele também alerta que, no Windows 10 22H2, o `D3D12Core.dll` de caixa para em SM 6.5; nesse caso é preciso encenar o Agility SDK em `D3D12\`, ao lado de `xcl.exe`, em `%LOCALAPPDATA%\XCL\D3D12\`, ou via `XCL_D3D12_DIR`. Como aqui é Windows 11 build 26300, isso **pode** não ser necessário — mas ainda precisa ser confirmado pelo preflight/`xcl run`, porque não foi medido.

---

## 3. Render distance, culling e resolução

### 3.1 Negativo verificado [V]

Não existe nenhuma variável de ambiente em XCL ou victory-fork com `DISTANCE`, `DRAW`, `RENDER`, `LOD`, `CULL`, `FSR`, `UPSCAL` ou equivalente para distância de desenho. Também não há switch de runtime para FSR/upscaling.

O que existe:

| Controle | Padrão | Onde |
|---|---:|---|
| `window_width` / `window_height` do XCL | 1280 × 720 | `xcl/crates/xcl/src/config.rs:184-185` |
| `XCL_WINDOW_POS` | auto/center/x,y | `hle/d3d12x/present.rs:464-469` |
| `VICTORY_DISPLAY_MODE` | **3840 × 2160** | `victory-gpu/src/dxgi.rs:222-237` |
| `VICTORY_BVH_BUILDER` | desligado; `1` autoriza o caminho de BVH do título | `victory-gpu/src/pipeline.rs:216,287` |
| `VICTORY_MESH_TEST`, `VICTORY_SCISSOR_TEST` | diagnóstico, não produção | fonte do victory-gpu |

O título vem de um build `Gaming.Xbox.Scarlett.x64` e o census mostra `XboxBvhBuilder.dll` com 7 imports ausentes. Mesmo com `VICTORY_BVH_BUILDER=1`, o fork marca ray tracing como incompleto. Portanto **iluminação em sombra e sombras RT não devem ser esperadas**.

### 3.2 O que é externo [D]

O sample público `Dynamic LOD` do GDK faz frustum culling e seleção de LOD **dentro de amplification + mesh shaders**, com a CPU apenas fornecendo instâncias. Ou seja: "render distance" em Series é lógica do jogo/GPU, não uma opção do sistema operacional nem uma variável do compatibilizador. A RTX 3070 tem mesh shaders (Turing+), então o caminho de geometria do XCL é elegível; o victory-fork, porém, ainda está incompleto nessa área.

Fontes:
- `learn.microsoft.com/en-us/samples/microsoft/xbox-gdk-samples/dynamiclod`
- `xcl/README.md:89-106` (SM 6.6 + mesh shaders; GPU sem mesh shaders não desenha a geometria NGG)
- `docs.xbox.com/.../raytracing` (BVH via LBVH/treelet e Embree no console)

### 3.3 Ajuste aplicado [V]

`VICTORY_DISPLAY_MODE=1280x720` foi adicionado ao perfil. Sem isso, o victory-fork dizia ao título que o monitor era 4K — um erro de dimensão que aumenta render targets sem aumentar qualidade percebida numa janela de teste. A faixa aceita pelo código é 640–7680 × 360–4320. Não mexemos em culling/LOD porque não há controle verificável; qualquer "distância menor" seria [H].

---

## 4. Cache

### 4.1 Caminhos verificados [V]

| Cache | Padrão / valor do perfil | Fonte |
|---|---|---|
| DXIL traduzido | `%LOCALAPPDATA%\xcl\dxil` (`XCL_DXIL_CACHE`) | `dxc.rs:566-573` |
| PSO do driver | dentro da pasta DXIL, subpasta `driver` (`XCL_PSO_DISK_CACHE`) | `real.rs:3916,3934` |
| Tradução XCL | `XCL_TRANSLATED_CACHE`, chave inclui backend, heap windows, per-lane e ID do compilador | `pso.rs:171-246` |
| victory-fork | `VICTORY_CACHE_DIR=F:\victory-cache`, `VICTORY_DATA_DIR=F:\victory-data` | `victory-core/src/config.rs:88-95` |
| Biblioteca de PSO victory | mapeada, handle aberto, trim ligado, lock desligado | `pipeline/library.rs` (tabela acima) |

A identidade do cache victory inclui exe BLAKE3, limites de loop/view, `VICTORY_GROUP_SYNC`, wave e bits de `VICTORY_SHADER_OPT`. Por isso **não alternar `SHADER_OPT`/`GROUP_SYNC` entre execuções comparativas sem esperar repovoar o cache** — o resultado de performance ficaria contaminado.

### 4.2 Recomendação externa [D]

Microsoft e NVIDIA recomendam criar PSOs em threads de fundo, usar PSO libraries, manter campos "don't care" com valores iguais para aumentar reuso e evitar compilação na primeira vez que um shader aparece. O mecanismo novo *Advanced Shader Delivery* (SODB → PSDB) promete shaders pré-compilados por hardware, mas exige integração do motor e publicação pelo Xbox Partner Center — não se aplica a um título unpacked já pronto.

Fontes:
- `learn.microsoft.com/en-us/samples/microsoft/directx-graphics-samples/d3d12-pipeline-state-cache-sample-win32`
- `developer.nvidia.com/blog/advanced-api-performance-pipeline-state-objects`
- `devblogs.microsoft.com/directx/advanced-shader-delivery-whats-new-at-gdc-2026/`

### 4.3 Ajuste aplicado [V]

- `XCL_DXC_DIR` passou a apontar para o DXC 1.8.2502 local.
- `pso-precompile.ps1` e `measure.ps1` agora chamam `Set-ProfileEnv`, então usam o mesmo DXC/thread budget do runtime.
- O comando de precompilação permanece `xcl shaders <rpf> --precompile --pipelines`.

---

## 5. Memória, resident slots e memory swap

### 5.1 Diferença de arquitetura [V] + [D]

Xbox Series X|S é UMA/cache-coherent: CPU e GPU compartilham 16 GB (13,5 GB utilizáveis pelo título em configurações típicas) e o DirectStorage usa descompressão por hardware (zlib, BCPack, swizzle) com buffer fixo e in-place. A RTX 3070 é discreta: 8 GB de VRAM mais 24 GB de RAM, dois pools de orçamento (`LOCAL`/`NON_LOCAL`).

No D3D12 do Windows, GPU não tem page fault: o app deve manter recursos residentes. Se passar do orçamento, o kernel pode **rebaixar heaps de VRAM para RAM como último recurso** e o processo pode ser congelado intermitentemente. Heaps não podem ser parcialmente residentes; `
Evict` não recupera orçamento de forma confiável em certas arquiteturas. A estratégia recomendada é "classify, budget and stream".

Fontes:
- `learn.microsoft.com/en-us/windows/win32/direct3d12/residency`
- `learn.microsoft.com/en-us/windows/win32/direct3d12/memory-management-strategies`
- `docs.xbox.com/build/console-features/storage/directstorage/directstorage-overview`
- `devblogs.microsoft.com/directx/directstorage-1-1-now-available/`

DirectStorage no Windows 1.1 usa **GDeflate** com descompressão na GPU quando o destino é recurso D3D12; se o destino é memória do sistema, usa CPU. Não é o mesmo formato do BCPack do console. O XCL implementa BCPack para os dados do título, então a tradução do caminho de streaming é necessária — não há uma env var que "ative" isso.

### 5.2 Controles verificados no victory-fork [V]

| Variável | Padrão real no código | Efeito / onde |
|---|---:|---|
| `VICTORY_TEXTURE_BUDGET_MB` | unset | Se definida, **substitui** a checagem de orçamento da VRAM para cópias de textura: `filled < limite` (`resource.rs:1357-1377`) |
| `VICTORY_TEXTURE_ROOM_PERCENT` | 90, clamp 50–97 | Fração do orçamento da placa usada como limite; o comentário diz que 94 devolve ~449 MB (`resource.rs:1387-1402`) |
| `VICTORY_TEXTURE_STAGE_MB` | 192 | Backlog de texturas em estágio (`draw.rs:1579-1583`) |
| `VICTORY_CARD_BUFFERS` | ligado | Cópias de buffers grandes do título ficam na VRAM; `0` mantém na RAM (`shadow.rs:106-139`) |
| `VICTORY_CARD_BUFFERS_MIN_MB` | **1** (o comentário acima diz 16, mas o código usa 1) | Menor buffer que vai para VRAM (`shadow.rs:142-158`) |
| `VICTORY_CARD_BUFFERS_MB` | **1024** (o comentário acima diz 512, mas o código usa 1024) | Teto dessas cópias (`shadow.rs:142-158`) |
| `VICTORY_WINDOW_KB` | 4096, mínimo 64, potência de dois | Janela de alocação/`trim` (`shadow.rs:2864-2877`) |
| `XCL_UMA_MB` | 2048 | Janela de memória GPU-visitável pelo CPU (`xcl/.../gpu/uma.rs:733-737`, `config.rs:27-30`) |

O comentário em `resource.rs:1362-1363` é explícito: passar do orçamento faz o sistema mover memória da placa para o PC e isso **encerras as runs 20 e 24** do desenvolvimento do fork. É a descrição mais direta de "memory swap" que existe no código.

### 5.3 Perfil aplicado [V]

| Preset | `VICTORY_CARD_BUFFERS_MB` | `VICTORY_TEXTURE_BUDGET_MB` | Intenção |
|---|---:|---:|---|
| `safe` | 256 | 3072 | deixar margem em 8 GB de VRAM e 24 GB de RAM |
| `speed` | 1024 | 4096 | combinação documentada no README do fork; não medida aqui |

Pagefile atual: 17.408 MB. Recomendação mantida: **48–64 GB**. Isso não foi alterado porque exige administrador e reinício. Com 24 GB de RAM e um título que já ocupou ~24 GB no upstream, 17 GB de pagefile é o limite mais fraco do sistema.

### 5.4 O que **não** fazer [H]

- Aumentar `VICTORY_TEXTURE_BUDGET_MB` achando que "mais cache é melhor": o limite existe justamente para evitar demotion/swap.
- Usar `VICTORY_GROUP_SYNC=0` para "ganhar tempo": o próprio fonte avisa que pode travar o card no Windows/NVIDIA.
- Ativar `VICTORY_BVH_BUILDER=1` esperando sombras corretas: o fork marca RT como incompleto e há 7 imports de `XboxBvhBuilder.dll` ausentes.
- Mudar flags de tradução entre execuções de benchmark sem repovoar o cache.

---

## 6. Estado do bundle

`bin\ScarletVanguard.exe` não precisa ser reconstruído por causa das variáveis: ele contém o PE do título e chama `scripts\optimize-rtx3070.ps1`, que lê `configs\rtx3070-24gb.env.ps1` na inicialização. Alterações de config já valem na próxima abertura. A reconstrução continua disponível via `.\scripts\build-bundle.ps1`.

---

## 7. Fontes consultadas

**Locais (desta árvore)**
- `xcl/README.md:87-107` — requisitos, DXC, Agility SDK, wave32
- `xcl/docs/wave64-on-wave32.md`
- `xcl/crates/xcl/src/hle/d3d12x/pso_async.rs`
- `xcl/crates/xcl/src/hle/d3d12x/real.rs`
- `xcl/crates/xcl/src/gpu/recomp/dxc.rs`, `pso.rs`
- `victory-fork/crates/victory-shader/src/emit/mod.rs`
- `victory-fork/crates/victory-gpu/src/{pipeline/make.rs,pipeline/library.rs,resource.rs,shadow.rs,dxgi.rs}`
- `victory-fork/README.md` — estado real de FPS/RT

**Externas**
- https://github.com/microsoft/GDK — diferença GDK vs GDKX (driver gráfico e compilador de shader)
- https://learn.microsoft.com/en-us/windows/win32/direct3d12/residency
- https://learn.microsoft.com/en-us/windows/win32/direct3d12/memory-management-strategies
- https://learn.microsoft.com/en-us/samples/microsoft/directx-graphics-samples/d3d12-pipeline-state-cache-sample-win32
- https://developer.nvidia.com/blog/advanced-api-performance-pipeline-state-objects
- https://devblogs.microsoft.com/directx/advanced-shader-delivery-whats-new-at-gdc-2026/
- https://learn.microsoft.com/en-us/samples/microsoft/xbox-gdk-samples/dynamiclod
- https://docs.xbox.com/build/console-features/storage/directstorage/directstorage-overview
- https://devblogs.microsoft.com/directx/directstorage-1-1-now-available/
- https://github.com/xcl-dev/xcl/blob/main/docs/wave64-on-wave32.md

---

## 8. O que ainda falta medir

1. `xcl run` / `victory run` com o título, para ver se preflight reclama de SM 6.6/DXC/Agility.
2. Captura PIX/VTune/ETW para separar: compilação de PSO, transferência RAM↔VRAM, shaders wave64 e job system.
3. p50/p95/p99 a 1280x720 com `safe` e só depois comparar `speed`.
4. Pagefile 48–64 GB (admin + reboot).
5. Instalar PIX, VTune e Windows Performance Toolkit (`xperf` está ausente; `wpr.exe` existe).

Até que isso aconteça, qualquer número de FPS — inclusive os 2–4 fps do README do victory-fork e os ~28–30 fps do XCL — é **relato upstream, não resultado desta máquina**.
