# XRMLX-Fusion

Versão **achatada** do XRMLX. Os três projetos que antes viviam como submodules separados — **Leonida Continued**, **XCL** e **victory-fork** — foram copiados para um único diretório, sem `.git` interno e sem estrutura de submodule.

> **Aviso importante.** Esta é uma árvore de trabalho **privada e não redistribuível**. Cada componente mantém sua licença original em `leonida/LICENSE`, `xcl/LICENSE` e `victory-fork/LICENSE`. Como o XCL é `GPL-2.0-only` e os outros dois são GPLv3, juntá-los numa única obra derivada não é legalmente distribuível. A fusão foi feita apenas para experimento local, conforme solicitado.

## O que é — e o que não é

É uma **justaposição física** dos três fontes em pastas com nomes distintos, com um único repositório Git no topo e um build unificado.

**Não é uma integração funcional.** Leonida é C++/Python com Vulkan/SPIR-V, XCL é Rust com D3D12.X/DXIL e victory-fork é Rust com D3D12/DXIL. Fundir essas implementações em um único executável exigiria reescrever manualmente uma das arquiteturas; isso não aconteceu aqui.

## Origem dos arquivos

| Pasta | Origem (snapshot 24/11/2026) | HEAD de origem |
|---|---|---|
| `leonida/` | `ubiquitarianism/leonida` + submodule `rpf9-explorer` | `4a50cafc7af8b5a842c85a3794b286232495068d` |
| `xcl/` | `xcl-dev/xcl` | `1d027e5c76c9a86a7ee2ca1cc677edf0be95d331` |
| `victory-fork/` | `fxceless/victory-fork` | `c96b4f2b8365bf43d54ab074940923ef8267f4c6` |

O agregado original com histórico e submodules continua intacto em `..\XRMLX`.

## Estrutura

```
XRMLX-Fusion/
├── README.md
├── .gitignore
├── scripts/
│   ├── validate.ps1   # confere estrutura, ausência de .git interno e de dados de jogo
│   ├── refresh.ps1    # recopia os fontes de ..\XRMLX para cá
│   ├── msvc.ps1       # inicializa o ambiente C++ (vcvars64) para cargo
│   ├── build.ps1      # build unificado dos três componentes
│   └── nvidia.ps1     # perfil NVIDIA: seleção de adaptador + launch
├── leonida/           # fonte achatado, inclui third_party/rpf9-explorer
├── xcl/               # fonte achatado
└── victory-fork/      # fonte achatado
```

## Uso rápido (PowerShell)

```powershell
cd C:\Users\rafae\Desktop\XRMVI\XRMLX-Fusion
.\scripts\validate.ps1
.\scripts\build.ps1 -Component leonida          # só checa ferramentas/ajuda
.\scripts\build.ps1 -Component xcl
.\scripts\build.ps1 -Component victory-fork
.\scripts\build.ps1 -Component all -GameDir "D:\seu\jogo"
.\scripts\nvidia.ps1 -Component xcl -GamePath "D:\seu\jogo"
.\scripts\nvidia.ps1 -Component victory-fork -GamePath "D:\seu\jogo\game_scarlett_master.exe"
```

Um build real exige uma **cópia do jogo pertencente ao usuário**; nenhum arquivo de jogo foi copiado para esta árvore.

## Integração D3D12 / toolchain C++ / perfil NVIDIA

- `msvc.ps1` localiza o Visual Studio Build Tools via `vswhere`, aplica `vcvars64.bat` ao processo e coloca `%USERPROFILE%\.cargo\bin` no `PATH`. É isso que permite ao `cargo` encontrar `cl.exe`/`link.exe` sem abrir um *Developer Prompt*.
- `build.ps1` já invoca essa inicialização antes de compilar XCL e victory-fork.
- **XCL** já suportava seleção de GPU via `XCL_ADAPTER` (índice ou substring do nome, ex.: `nvidia`).
- **victory-fork** recebeu uma mudança pontual em `crates/victory-gpu/src/host.rs`: nova variável `VICTORY_ADAPTER` com a mesma semântica, ignorando adaptadores de software (WARP). O padrão continua sendo `auto`.
- `nvidia.ps1` define `XCL_ADAPTER=nvidia`, `VICTORY_ADAPTER=nvidia` e força `VICTORY_HOST_WAVE=32` / `VICTORY_HOST_MAX_WAVE=32`, que é o caminho de wave64-em-32-lanes usado em GPUs NVIDIA.

### O que foi realmente verificado

- Rust `stable-x86_64-pc-windows-msvc` 1.100.0 instalado; `cargo`, `rustc`, `cl` e `link` disponíveis.
- `cargo build --release --workspace` no XCL: **concluído**, `xcl\target\release\xcl.exe` (8,57 MB), `--help` executado.
- `cargo build --release -p victory` no victory-fork: **concluído**, `victory-fork\target\release\victory.exe` (7,46 MB), `--help`/`--version` executados. O fork recebeu 4 commits novos (`2119b0e` → `c96b4f2`: depth bounds, defaults mais rápidos, texturas de pele/cabelo e correções de forks colaboradores); a árvore foi sincronizada e o patch `VICTORY_ADAPTER` reaplicado sobre `host.rs`. Avisos do compilador no XCL (`fetch_update` deprecado) não foram alterados.
- **Não foi executado nenhum teste de GPU e nenhum jogo**: não há jogo fornecido nesta máquina. "Funcional para NVIDIA" aqui significa toolchain + seleção de adaptador + binários compilados prontos para o jogo do usuário, **não** uma sessão de gameplay validada.

## Pré-requisitos nesta máquina

- Presentes: Git, Python, CMake, .NET, Node, ffmpeg, `vulkaninfo`, Rust/MSVC, `cl`/`link`.
- Ainda ausentes para o Leonida: `spirv-val`, `llvm-mc` com alvo AMDGPU no PATH.
- `C:` tem ~189 GB livres: o build completo do Leonida (~400 GB) não cabe; use `--stages check,shaders,runtime` (~30 GB), de preferência com `--work` em outra unidade.
