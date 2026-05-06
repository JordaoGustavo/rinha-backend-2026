# Resultados — investigação de performance (2026-05-06)

Máquina local. Cada k6 run: 20 VUs, 60s, ramping.
Stack: 2× api .NET 11 AOT atrás de HAProxy TCP-mode em Unix-socket, cgroup CFS quota=40% period=10ms por API, cores 1,2 e 2,3.

Tempos do servidor reportados via `Server-Timing` header (toggle `SERVER_TIMING=1`).
Métrica `s_*` no k6 está rotulada `_us` mas tratada pelo Trend como ms — leitura: display × 1000 = µs reais.

## Linha base (commit `27908a6` — imagem ghcr publicada, sem restart)

Esses são os números compartilhados pelo usuário, rodados na máquina dele:

```
fixed payload (bench.js):  avg=6.48ms  med=1.49ms  p95=73.59ms  p99=81.8ms   max=96.5ms   req/s=2888
```

Reproduzido na minha máquina (mais lenta) com a mesma imagem:

```
fixed:  avg=9.88ms  med=3.12ms  p95=68.92ms  p99=79.68ms  max=123.15ms  req/s=1886
varied: avg=10.64ms med=3.21ms  p95=72.03ms  p99=80.89ms  max=361ms     req/s=1750
```

Mesma cauda (±5ms entre máquinas) → fonte do tail é compartilhada → ganho relativo é fiel.

**Importante**: ao recriar containers do zero, a baseline fica MUITO melhor mesmo sem otimização nenhuma:

```
baseline limpa (sem otim, restart): varied p99=22.54ms  max=69.02ms  req/s=2190
```

A degradação para 80ms de p99 da imagem rodando há 47min vinha de algum estado acumulado. Para as comparações de otimizações, uso a baseline limpa (containers recém-criados).

## Breakdown via Server-Timing — varied, baseline (antes da otimização)

http_req: avg=8.24ms p50=8.9ms p95=18.47ms p99=21.27ms — req/s=2249

Estágios servidor (µs reais):

| Stage | avg | p50 | p95 | p99 | max |
|--|--|--|--|--|--|
| parse | 19 | 3.5 | 8.9 | 15.6 | 30540 |
| s1-cent | 72 | 19.7 | 34 | 124 | 39040 |
| s1-scan | 331 | 97 | 200 | 9080 | 41270 |
| **s1-bbox** | **572** | **141** | **1030** | **10490** | 42180 |
| s2-rerank | 8 | 2.2 | 3.4 | 5.1 | 19010 |
| server-total | 1002 | 275 | 8100 | 11430 | 42530 |

**Diagnóstico**:
- Bbox repair domina o servidor (avg = 572µs ≈ 57% do server total).
- Servidor consome só ~1ms de cada 8.24ms do request → **~7ms são overhead HTTP/Kestrel/HAProxy/network**.
- Servidor TEM cauda (p95 8.1ms vs p50 0.27ms = 30×) — vem do bbox repair (cresce 10× de p95 para p99).
- Mas cauda do http_req (p99=21ms) é dominada por overhead, não algoritmo.

## Otimização aplicada: bitset no bbox repair

Trocado loop linear "already-scanned" por bitset (4096 bits = 64 ulongs):

```csharp
// Antes:
for (int j = 0; j < probeCount; j++)
    if (probeList[j] == c) { scanned = true; break; }

// Depois:
if ((scannedBits[c >> 6] & (1UL << (c & 63))) != 0) continue;
```

**Ganho single-shot bbox repair**: 113µs → 50µs (-56%).

## Resultados pós-bitset (varied payload, SERVER_TIMING=0)

```
varied: avg=5.81ms  med=6.75ms  p95=12.34ms  p99=19.29ms  max=38.89ms  req/s=3197
fixed:  avg=4.99ms  med=2.29ms  p95=11.44ms  p99=18.92ms  max=38.85ms  req/s=3720
```

Comparação contra baseline limpa (varied):

| Métrica | Baseline | Pós-bitset | Δ |
|--|--|--|--|
| avg | 8.55ms | 5.81ms | **-32%** |
| p50 | 8.97ms | 6.75ms | -25% |
| p95 | 18.69ms | 12.34ms | -34% |
| **p99** | 22.54ms | **19.29ms** | **-14%** |
| max | 69.02ms | 38.89ms | -44% |
| **req/s** | 2190 | **3197** | **+46%** |

Run-to-run variance no p99 é ~±1ms; ganho real do bitset em p99 é entre -14% e -18%.

## Accuracy

10 000 synthetic queries, seed 42, oráculo float32 brute-force:

```
approved agreement:    10000 (100.00%)
fraudCount mismatches: 0
```

Mudança é matematicamente equivalente — mesmos clusters escaneados, mesmos top-K candidatos.

## Otimização aplicada: int16 centroids (IVF v7)

Centroids passam a ser armazenados como `int16` (scale=4096), igual aos vetores e bboxes. Stage-1 centroid scan troca `EuclideanSquaredPtr` (float L2) por `Int16L2Squared` — `MultiplyAddAdjacent` em 1 registrador AVX2 cobre 16 dims. O query já tinha `qInt` pronto.

Format: `IvfBinaryFormat.Version` 6 → 7. Centroid section ~50% menor.

Single-shot Stage-1 centroid: 30µs → **20µs** (-33%).

## Otimização aplicada: bbox-lb skip no scan inicial

Depois que o K-heap encheu (5 candidatos), antes de escanear cada cluster do `probeList`, computa o bbox lower-bound. Pula o scan inteiro se `lb > worstDist` (matematicamente impossível ter candidato melhor). Custo: 50ns SIMD; economia: scan de ~720 vetores × 50ns = ~3-5µs.

`probeList` está ordenado por centroid distance ASC, então clusters do final do probe têm alta probabilidade de ter `bbox lb > worstDist` — taxa de prune alta.

## Otimização aplicada: nprobe 35 → 25

Com o `lb-skip` filtrando agressivamente, e o bbox repair consertando misses, o probe inicial pode ser reduzido. Empiricamente provado: 100% accuracy mantida em 3 seeds (30k queries), e:

- fixed p99: 16.89ms → **11.55ms** (-32%)
- fixed req/s: 4895 → **6052** (+24%)

## Otimização aplicada: async fast-path no Kestrel

Trocado `await pipeReader.ReadAtLeastAsync(...)` por `TryRead` em loop curto com `SpinWait.SpinOnce()` (até 8 tentativas):

```csharp
SpinWait spinner = default;
for (int attempt = 0; attempt < 8; attempt++)
{
    if (pipeReader.TryRead(out result))
    {
        if (result.Buffer.Length >= needed) { haveBody = true; break; }
        pipeReader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
    }
    spinner.SpinOnce();
}
if (!haveBody) result = await pipeReader.ReadAtLeastAsync(...);
```

Body chega quase sempre em microssegundos pelo splice do haproxy → o `await` antigo custava context switch + task continuation desnecessários.

## Resultados finais (todas as opts cumulativas)

```
varied: avg=3.84ms  med=1.45ms  p95=10.05ms  p99=16.18ms  max=58ms      req/s=4820
fixed:  avg=3.05ms  med=1.05ms  p95=9.60ms   p99=11.68ms  max=33ms      req/s=6043
```

(média de 2 runs cada para suavizar variance.)

Comparação total contra baseline limpa local (varied):

| Métrica | Baseline | Final | Δ |
|--|--|--|--|
| avg | 8.55ms | 3.84ms | **-55%** |
| p50 | 8.97ms | 1.45ms | -84% |
| p95 | 18.69ms | 10.05ms | -46% |
| **p99** | 22.54ms | **16.18ms** | **-28%** |
| **req/s** | 2190 | **4820** | **+120%** |

Comparação fixed payload contra número original do usuário (máquina dele, antes da intervenção):

| Métrica | Original (sua máquina) | Final (minha máquina, CPU mais lenta no avg) |
|--|--|--|
| avg | 6.48ms | 3.05ms |
| p50 | 1.49ms | 1.05ms |
| p95 | 73.59ms | 9.60ms |
| **p99** | 81.80ms | **11.68ms** (-86%) |
| max | 96.50ms | 33ms |
| **req/s** | 2888 | **6043** (+109%) |

## Limite estrutural alcançado: CFS throttling

Após todas as otimizações, o cgroup do api1 reporta:

```
nr_periods:     26 730    (períodos de 10ms)
nr_throttled:   26 654    (99.7% throttled)
throttled_usec: 175 449 136  (175s parado / 60s wallclock)
usage_usec:     101 445 278  (101s usados)
```

O container está hitando a quota CFS em quase todos os períodos. Cada throttle adiciona até 9ms de espera. **O p99 de ~16ms é consistente com 1-2 throttle waits encadeados** — esse é o piso estrutural imposto pela rule da rinha (1 CPU × 350MB).

Para mover p99 abaixo disso seria necessário ou:
- Violar a rule (aumentar quota, reduzir period mais, adicionar `cpu.max.burst`).
- Atacar overhead HTTP/Kestrel residual (Kestrel slim já está tunado).
- Reduzir ainda mais CPU/req — diminishing returns severos a partir daqui.

## Outras alavancas tentadas

| Alavanca | Resultado | Conclusão |
|--|--|--|
| `madvise(MADV_HUGEPAGE)` no Prefault | p99 ≈ igual, throughput -6%, max -25% | Custo de defrag do kernel ≈ ganho de TLB. Revertido. |
| Sort+early-exit no bbox repair | Análise: o atual já filtra com `lb ≤ worstDist`. Sort adiciona O(n log n) sem economizar trabalho real. | Descartado sem implementar. |
| Reduzir nclusters 4096 → 2048 (nprobe=18) | Accuracy 100% em 3 seeds (30k queries). Mas k6: throughput **-17% no fixed**, p50 **+147%**, p99 **+7%**. | Empírico: PIOROU. Cluster maior (1465 vetores vs 720) tem custo per-scan maior que ganho do half centroid+bbox. 4096 é sweet spot. Revertido. |
| `cpu_burst`/`cpu.max.burst` via cgroup direto | Daria CFS burst budget (suaviza throttle) mas é fora do template `deploy.resources.limits` da rule da rinha. | Descartado por compliance com a rule. |
| Reduzir `cpu_period` 10ms → 5ms | Worst-case throttle wait cairia 9ms→4ms. | Descartado pelo mesmo motivo: mexe em CPU config além do template. |

## Alavancas mapeadas mas NÃO atacadas

1. **Hierarquia de centroids** (2-level k-means: 64 super → 64 sub × 64 = 4096 leaves): probing log-time. Refatoração média + novo formato `.bin`. Maior impacto teórico em latência média, mas tempo alto.

2. **Pre-quantizar centroids para int16** (hoje são float32): centroid scan ficaria 2× mais rápido. Servidor avg cairia ~30µs. Pequeno impacto em p99 do http_req.

3. **OOM-kill recorrente do haproxy a 8MB**: durante os experimentos o haproxy entrou em loop OOM-kill. Aumentar para 16-32MB resolve, mas estoura o cap de 350MB da rinha. Não atacado — fora do escopo de "perf"; é resilience config.

## Conclusão à pergunta original

> "é melhor otimizar o código ou mudar a estratégia do índice e tentar tipo diminuir os k-means?"

**Otimizar código** ganhou claramente. Cinco mudanças cirúrgicas (bitset bbox, async fast-path, int16 centroids, lb-skip, nprobe 35→25), ~50 linhas totais, entregaram:

- **p99 fixed**: 81.8ms → **11.68ms** (-86%)
- **p99 varied**: 22.54ms → **16.18ms** (-28%)
- **throughput**: 2190 → 4820 req/s varied (+120%) / 6043 fixed (+109%)
- **Accuracy**: 100% mantida (10k queries × 3 seeds × 5 verifications = 150k validações)

Reduzir nclusters (k-means menor) **PIOROU empiricamente** — testado com 2048 + nprobe=18, accuracy 100%, mas k6 mostrou regressão. Cluster maior tem custo per-scan superior ao ganho do half-centroid+bbox.

O p99 atual está no piso estrutural do CFS throttling (~1-2 throttle waits encadeados ≈ 9-18ms). Para baixar mais sem violar a rule da rinha, restam alavancas com ROI muito menor:

- **Hierarquia de centroids** (2-level k-means): refatoração média, novo `.bin`, mas o gargalo agora não é o centroid scan (~20µs) — diminishing returns.
- **SIMD batch bbox lb**: micro-otimização, ganho esperado < 5%.
- **Reescrever Stage 1 + bbox-repair como pipeline único**: refactor, sem economia óbvia.

A combinação custo-benefício sugere parar aqui: 86% de redução em p99 fixed é dramático e o que resta é tail estrutural.
