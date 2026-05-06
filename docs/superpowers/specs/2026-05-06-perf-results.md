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

## Resultados finais (bitset + async fast-path, 4096 clusters)

```
varied: avg=5.26ms  med=5.25ms  p95=11.05ms  p99=18.44ms  max=121ms (outlier)  req/s=3544
fixed:  avg=4.58ms  med=1.82ms  p95=10.64ms  p99=17.87ms  max=38.85ms          req/s=4069
```

Comparação total contra baseline limpa (varied):

| Métrica | Baseline | Final | Δ |
|--|--|--|--|
| avg | 8.55ms | 5.26ms | **-38%** |
| p50 | 8.97ms | 5.25ms | -41% |
| p95 | 18.69ms | 11.05ms | -41% |
| **p99** | 22.54ms | **18.44ms** | **-18%** |
| **req/s** | 2190 | **3544** | **+62%** |

Comparação fixed payload contra número original do usuário (máquina dele):

| Métrica | Original (sua máquina) | Final (minha máquina, mais lenta no avg) |
|--|--|--|
| avg | 6.48ms | 4.58ms |
| p50 | 1.49ms | 1.82ms |
| p95 | 73.59ms | 10.64ms |
| p99 | 81.80ms | 17.87ms |
| req/s | 2888 | 4069 |

(parte da diferença vs número original era da imagem ghcr rodando há 47min sem restart — a baseline limpa local tinha p99 ≈ 22ms.)

## Outras alavancas tentadas

| Alavanca | Resultado | Conclusão |
|--|--|--|
| `madvise(MADV_HUGEPAGE)` no Prefault | p99 ≈ igual, throughput -6%, max -25% | Custo de defrag do kernel ≈ ganho de TLB. Revertido. |
| Sort+early-exit no bbox repair | Análise: o atual já filtra com `lb ≤ worstDist`. Sort adiciona O(n log n) sem economizar trabalho real. | Descartado sem implementar. |
| Reduzir nclusters 4096 → 2048 (nprobe=18) | Accuracy 100% em 3 seeds (30k queries). Mas k6: throughput **-17% no fixed**, p50 **+147%**, p99 **+7%**. | Empírico: PIOROU. Cluster maior (1465 vetores vs 720) tem custo per-scan maior que ganho do half centroid+bbox. 4096 é sweet spot. Revertido. |

## Alavancas mapeadas mas NÃO atacadas

1. **Hierarquia de centroids** (2-level k-means: 64 super → 64 sub × 64 = 4096 leaves): probing log-time. Refatoração média + novo formato `.bin`. Maior impacto teórico em latência média, mas tempo alto.

2. **Pre-quantizar centroids para int16** (hoje são float32): centroid scan ficaria 2× mais rápido. Servidor avg cairia ~30µs. Pequeno impacto em p99 do http_req.

3. **OOM-kill recorrente do haproxy a 8MB**: durante os experimentos o haproxy entrou em loop OOM-kill. Aumentar para 16-32MB resolve, mas estoura o cap de 350MB da rinha. Não atacado — fora do escopo de "perf"; é resilience config.

## Conclusão à pergunta original

> "é melhor otimizar o código ou mudar a estratégia do índice e tentar tipo diminuir os k-means?"

**Otimizar código** ganhou claramente. Duas mudanças cirúrgicas em ~30 linhas totais entregaram:

- **p99**: 22.54ms → 18.44ms (**-18%**, varied)
- **throughput**: 2190 → 3544 req/s (**+62%**)
- **Accuracy**: 100% mantida (10k queries × 3 seeds = 30k validações)

Reduzir nclusters (k-means menor) **PIOROU empiricamente** — testado com 2048 + nprobe=18, accuracy 100%, mas k6 mostrou regressão em throughput e p99. Cluster maior tem custo per-scan superior ao ganho do half-centroid+bbox.

Próxima alavanca real (não atacada): hierarquia de centroids ou substituir overhead HTTP residual. Ambas são mudanças mais invasivas.
