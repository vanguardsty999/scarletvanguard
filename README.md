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
| `victory-fork/` | `fxceless/victory-fork` | `2119b0e44c53aa9648307b0c5c73590290305b1f` |

O agregado original com histórico e submodules continua intacto em `..\XRMLX`.

## Estrutura

```
XRMLX-Fusion/
├── README.md
├── .gitignore
├── scripts/
│   ├── validate.ps1   # confere estrutura, ausência de .git interno e de dados de jogo
│   ├── refresh.ps1    # recopia os fontes de ..\XRMLX para cá
│   └── build.ps1      # build unificado dos três componentes
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
```

Um build real exige uma **cópia do jogo pertencente ao usuário**; nenhum arquivo de jogo foi copiado para esta árvore.

## Pré-requisitos nesta máquina

- Presentes: Git, Python, CMake, .NET, Node, ffmpeg, `vulkaninfo`.
- Ausentes: `cargo`/`rustc`/`rustup` (XCL e victory-fork), `spirv-val`, `llvm-mc` com alvo AMDGPU e `cl` no PATH (Leonida).
- `C:` tem ~189 GB livres: o build completo do Leonida (~400 GB) não cabe; use `--stages check,shaders,runtime` (~30 GB), de preferência com `--work` em outra unidade.
