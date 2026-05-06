# Resultados — investigação de performance (2026-05-06)

Máquina local. Cada k6 run: 20 VUs, 60s, ramping. Stack: 2× api .NET 11 AOT atrás de HAProxy TCP-mode, cgroup CFS quota=40% period=10ms por API, cores 1,2 e 2,3.

Tempos do servidor reportados via `Server-Timing` header (toggle `SERVER_TIMING=1`).
Métrica `s_*` do k6 está rotulada `_us` mas é tratada pelo Trend como ms — leia o display × 1000 = µs reais. (Bug cosmético no `bench-varied.js`, corrigir em iteração futura.)

## Baseline (commit `27908a6` ghcr publicada — sem Server-Timing)

| Carga | avg | med | p95 | p99 | p99.9 | max | req/s |
|--|--|--|--|--|--|--|--|
| Fixed payload (k6 original) | 9.88ms | 3.12ms | 68.92ms | 79.68ms | 86.7ms | 123.15ms | 1886 |
| Varied payload | 10.64ms | 3.21ms | 72.03ms | 80.89ms | 91.47ms | 361.11ms | 1750 |

Comparação contra número reportado pelo usuário (máquina dele): cauda p95/p99 idêntica (±5ms), avg/med ~3× mais lentos (CPU diferente). **A fonte do tail é a mesma na minha máquina.**

## Baseline + Server-Timing (varied payload, SERVER_TIMING=1)

http_req: avg=8.24ms med=8.9ms p95=18.47ms p99=21.27ms max=89.79ms — req/s=2249

Estágios servidor (µs):

| Stage | avg | p50 | p95 | p99 | max |
|--|--|--|--|--|--|
| parse | 19 | 3.5 | 8.9 | 15.6 | 30540 |
| s1-cent | 72 | 19.7 | 34 | 124 | 39040 |
| s1-scan | 331 | 97 | 200 | 9080 | 41270 |
| **s1-bbox** | **572** | **141** | **1030** | **10490** | 42180 |
| s2-rerank | 8 | 2.2 | 3.4 | 5.1 | 19010 |
| server-total | 1002 | 275 | 8100 | 11430 | 42530 |

**NB**: a baseline com SERVER_TIMING ON melhorou versus a baseline com a imagem ghcr publicada (p99 21ms vs 80ms). Run-to-run variance da máquina ou efeito de containers recém-criados (caches frios diferentes). Vou re-rodar SERVER_TIMING=0 com a imagem local para ter baseline limpo.

## Diagnóstico

- **Algoritmo tem cauda séria no bbox repair**: p95→p99 cresce 10× (1ms → 10.5ms). Atacar isso é o maior ganho potencial em código de path normal.
- **Overhead HTTP**: avg http_req (8.24ms) − avg server (1ms) = **~7ms de Kestrel/HAProxy/k6/network**. Aparece em todo request, mas no p99 a contribuição é ~10ms.
- **Servidor é 12% do request médio.** Para mover p99 do request, precisa atacar AMBOS overhead e cauda algorítmica.
