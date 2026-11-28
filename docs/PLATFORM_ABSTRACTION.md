# Camada de abstração de plataforma (XDK → Win32)

Objetivo: separar **o que o título pede** (XDK/Xbox) de **como o PC responde** (Win32/D3D12), substituindo aos poucos os stubs por implementações nativas. Nada aqui depende de alterar o jogo; toda a mudança acontece nos componentes `xcl/` e `src/victory/`.

## Duas trilhas

### A. Com fonte disponível — abstração dentro do runtime

Tanto XCL quanto src/victory já têm o ponto de separação natural:

- XCL: `crates/xcl/src/hle/` — `kernelx`, `XGameRuntime`, `d3d12x`, `xaudio2`, `vfs`, `memory`, `tls`.
- src/victory: `crates/victory-hle/src/modules/` — o mesmo papel, com `d3d12x`, `files`, `audio`, `lifecycle`, `xmem` etc.

Cada chamada do título entra por um desses módulos. A regra é: **a interface continua sendo a do console; a implementação por trás vira Win32 nativa**.

| Subsistema XDK | Substituto Win32 / nativo | Onde mexer |
|---|---|---|
| `kernelx` (thread, sync, tempo) | `kernel32`/`synchapi`, `QueryPerformanceCounter`, `CreateThread` | `hle/kernelx`, `modules/lifecycle` |
| `XGameRuntime` | inicialização de processo, package identity offline, relógio de frame | `hle/xgameruntime`, `modules/runtime` |
| `D3D12.X` (`D3D12XboxCreateDevice`, `SetFrameIntervalX`) | D3D12 + DXGI reais, já com adaptador selecionável | `hle/d3d12x`, `victory-gpu` |
| `XAudio2` / `XMA` / `Opus` | XAudio2.4 PC ou WASAPI + decodificadores próprios | `hle/xaudio2`, `modules/audio` |
| `DirectStorage` / BCPack | DirectStorage PC ou descompressão própria em CPU | `hle/dstorage`, `modules/dstorage` |
| `XUser` / `XTitle` / licenças | usuário offline local (stub determinístico) | `hle/xgameruntime`, `modules/runtime` |
| Montagens `G:` / `T:` | Win32 CreateFile + overlay de escrita fora do diretório do jogo | `hle/vfs`, `modules/files` |
| `XMem` / heaps de console | `VirtualAlloc2`, heaps D3D12, `IDXGIAdapter3` para orçamento | `hle/memory`, `victory-gpu/host.rs` |

### B. Sem fonte — shim estilo Proton

Quando não há código-fonte para uma DLL do título, o caminho é o mesmo usado pelo Proton: **deixar o PE rodar nativamente e interceptar as importações**.

1. O loader já mapeia o executável e as DLLs (`victory-loader`, PE loader do XCL).
2. `victory census <exe>` / `xcl inspect <exe>` produzem a lista de `dll!export` e o que já é servido.
3. Cada importação não resolvida ganha primeiro um stub (com log), depois uma implementação nativa.
4. A implementação nativa entra atrás de um *feature flag*; o stub permanece como fallback até a substituição ser validada.

Isso evita a ilusão de "compatibilidade total": cada API é promovida individualmente, com evidência de que o título a chamou.

## Fluxo incremental

```text
census/inspect → priorizar por chamadas reais (ETW/PIX)
              → stub com log
              → implementação Win32 nativa (feature flag)
              → medir antes/depois
              → remover o stub quando não houver mais chamadas
```

## Regras para esta árvore

- Nenhuma alteração ao diretório do jogo; somente leitura.
- Nenhuma chamada de DRM, descriptografia ou chave.
- Implementações novas ficam no módulo HLE correspondente, não no loader.
- Todo stub novo registra a primeira chamada em log para alimentar o censo.
- Ao trocar um stub por nativo, registrar o antes/depois no runbook de performance.

## Censo real do título (já executado)

`xcl inspect` e `victory inspect` sobre `game_scarlett_master.exe` (114,61 MB, PE x64 de Scarlett) encontraram 587 imports de 68 DLLs, dos quais 73 são stubs:

| DLL | Stubs | Trilha recomendada |
|---|---|---|
| `libHttpClient.GDK.dll` | 38 | manter stub offline; baixa prioridade para FPS |
| `Bink2Scarlett.dll` | 23 | Bink 2 / vídeo — implementar após medir se é chamado no prólogo |
| `XboxBvhBuilder.dll` | 7 | BVH de ray tracing — depende do trabalho de RT do fork |
| `amd_fidelityfx_loader_xbox.dll` | 4 | FFX/upscaling — candidato a nativo |
| `xmem.dll` | 1 | completar o HLE de memória |

`victory census <exe> --missing` confirma os mesmos grupos (72 ausentes no victory: 38 libhttpclient, 23 Bink2Scarlett, 7 XboxBvhBuilder, 4 FFX; `xmem.dll` está coberto no victory). Relatórios: `measurements/inspect-game/{xcl-inspect,victory-inspect,victory-census}.txt`. Isto substitui a estimativa inicial: a priorização agora vem de contagem real de imports.

## Próxima ação concreta

```powershell
.\scripts\pso-precompile.ps1 -GameDir "N:\downloads\GTA6.Xbox.v1.0.2.1-CYBERLEEK"
.\scripts\measure.ps1 -Component xcl -GamePath ".\game_scarlett_master.exe" -Tool etw
.\scripts\measure.ps1 -Component src/victory -GamePath ".\game_scarlett_master.exe" -Tool etw
```

Com o `census` em mãos, a priorização deve usar contagem de chamadas por export, não intuição. A tabela acima vem de `xcl inspect` e `victory census`; a contagem de chamadas em runtime ainda exige ETW/PIX.
