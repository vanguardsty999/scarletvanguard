# Runbook de performance — medir antes de otimizar

Meta: sair de 10–20 fps para um caminho estável. A ordem é **medir → identificar → mudar uma coisa → medir de novo**.

## 1. O que medir

| Métrica | Ferramenta | O que procurar |
|---|---|---|
| Frame time p50/p95/p99 | PIX / ETW | picos periódicos vs. custo uniforme |
| GPU busy / stalls | PIX | espera por PSO, upload de textura, ray tracing |
| `Create*PipelineState` e compilação DXIL | PIX / `XCL_PSO_TRACE=1` | compilação **durante o gameplay** |
| CPU hotspots e contentão | VTune | `job system`, filas, locks, `WaitForSingleObject` |
| Migração de threads entre cores | ETW (`CSWITCH`, `PROC_THREAD`) | threads de job saltando de NUMA/core |
| Cache de shader/PSO | logs do XCL/src/victory | hit rate baixo → compilações repetidas |
| Memória commit / page faults | ETW / Task Manager | crescimento ~1 GB a cada poucos segundos |

## 2. Ferramentas

### PIX (GPU)
1. Abrir o `.exe` compilado como *target process*.
2. Capturar **um frame** no prólogo, com o jogo já aquecido e outro com cache frio.
3. Em *Pipeline / Events*, procurar `CreateGraphicsPipelineState`, `CreateComputePipelineState` e `CompileShader` dentro do intervalo do frame.
4. Salvar a captura em `measurements/<sessao>/pix/`.

### VTune (CPU)
1. *Hotspots* por 60–120 s no mesmo cenário.
2. Depois, *Threading* para ver wait time, contention e imbalance das filas de job.
3. Comparar o histograma de CPU do thread principal com os threads de trabalho.

### ETW (baixo overhead, sempre disponível)
`scripts\measure.ps1 -Tool etw` imprime os comandos exatos e cria a pasta da sessão. Sequência típica:

```powershell
wpr -start CPU -start GPU -filemode
# executar o jogo com as variáveis de trace do script
wpr -stop measurements\<sessao>\trace.etl
```

Analisar com WPA: gráfico de *CPU Usage (Precise)*, *GPU Usage* e eventos `Microsoft-Windows-D3D12`/`Microsoft-Windows-DXGI`.

## 3. Hipóteses priorizadas

### H1 — compilação de PSO em runtime
É o culpado mais comum em ports de Xbox. Sintomas: engasgos irregulares, picos de CPU em threads de driver, cache frio pior que cache quente.

Ação: **pré-compilar no build**.

```powershell
# XCL — gera blobs de driver para reuso na execução
xcl shaders <pack.rpf> --precompile --only cs --pipelines
# repetir para vs/ps conforme os estágios encontrados

# src/victory
victory translate <game dir> --validate --pipelines
```

`scripts\pso-precompile.ps1` apenas **prepara os comandos**; rode com `-Execute` quando o download terminar e você autorizar. Nenhum arquivo do jogo é escrito: os caches ficam no diretório de dados/cache do XCL/src/victory.

### H2 — job system e afinidade de threads
Sintomas: threads de job migrando entre cores, waits longos, p95 muito acima da mediana.

Ação: medir com VTune/ETW, depois usar `scripts\affinity.ps1 -ProcessName <exe> -Cores 0-7 -Apply` para fixar o processo/threads em um conjunto de cores. Experimentar separar:
- threads de compilação de PSO (poucos cores, baixa prioridade);
- threads de job de render (cores isolados, alta prioridade);
- thread de apresentação (um core dedicated).

O script é **somente leitura por padrão**; `-Apply` é necessário para alterar afinidade.

## 4. Variáveis já preparadas

| Componente | Variável | Efeito |
|---|---|---|
| XCL | `XCL_PSO_TRACE=1` | loga cada compilação de pipeline |
| XCL | `XCL_DRED=1` | breadcrumbs em caso de device removal |
| XCL | `XCL_FRAME_DUMP=2` | amostra frames e registra `non_dark_samples` |
| XCL | `XCL_ADAPTER=nvidia` | escolhe a GPU NVIDIA explicitamente |
| src/victory | `VICTORY_GPU_TIMING=1` | mede cada lista de submission |
| src/victory | `VICTORY_PIPELINE_WAIT_MS=24` | reduz espera por criação de pipeline |
| src/victory | `VICTORY_D3D12_DRED=1` | diagnóstico de falha de GPU |
| src/victory | `VICTORY_ADAPTER=nvidia` | patch local: escolhe a GPU NVIDIA |
| src/victory | `VICTORY_HOST_WAVE=32` | caminho wave64-em-32-lanes para NVIDIA |

## 5. Critério de aceite

Não mudar mais de uma variável por rodada. Uma rodada é válida quando:

1. PIX mostra **zero compilações de PSO** no frame medido (ou todas vindas do cache).
2. p95 de frame time cai em relação à linha de base.
3. VTune/ETW mostra menos migração de threads de job.
4. Nenhuma mudança foi feita no diretório do jogo.

Registrar sempre: commit, hardware, cenário, duração, comando exato e contagens reais. Se não foi medido, escrever "não medido" — não estimar.
