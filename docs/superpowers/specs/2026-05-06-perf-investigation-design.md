# Investigação de performance — Rinha de Backend 2026

Data: 2026-05-06

## Objetivo

Cortar p99 da API `/fraud-score` mantendo 100% de agreement com o oráculo float32 (`ExactDetector`), em dois cenários:

1. **k6 com payload fixo** (benchmark atual `scripts/k6/bench.js`)
2. **k6 com payloads variados** (mais próximo da rinha real)

## Baseline atual reportada

```
http_req_duration: avg=6.48ms  med=1.49ms  p(95)=73.59ms  p(99)=81.8ms  max=96.5ms
http_reqs:        193 554 (2 888.86/s)  http_req_failed: 0.00%
```

VUs=20, duration=60s, payload constante. 2 réplicas .NET 11 AOT atrás de HAProxy TCP-mode em Unix-socket.

## Hipóteses

1. **Variância de runtime domina o tail no benchmark fixo.** Payload é o mesmo em todos os 193k requests → trabalho do KNN é determinístico → p95/p50 ≈ 50× só pode vir de runtime: CFS throttling, TLB miss, GC, async path do Kestrel quando `pipeReader.TryRead` falha (`Program.cs:108-109`), contention nas cores 2,3 compartilhadas entre api1/api2.

2. **Algoritmo provavelmente domina o p99 sob payload variado.** O bbox repair pass (`IvfDetector.cs:253-270`) varre os 4096 clusters depois do scan inicial. Para queries no "exterior" do espaço, mais clusters passam o teste de lower-bound → mais scans extras → tail crescente.

3. **O bbox repair tem custo fixo desperdiçado.** Para cada um dos 4096 clusters, faz busca linear `for j in probeCount` no `probeList` para checar se já foi escaneado: `4096 × 35 ≈ 143k` comparações de int por request, sempre. Trocável por bitset.

## Plano de execução

### Fase 1 — Preparação & baseline
- Rodar `make download-resources` (~48MB) e `make preprocess` (k-means 4096 clusters, ~140s).
- `make docker-build` + `make docker-up` + `make k6` para registrar baseline na máquina local. Os números absolutos vão diferir do reportado (máquina diferente da rinha) mas o **delta** entre versões é fiel.

### Fase 2 — Instrumentação para gerar breakdown
- Adicionar **Server-Timing dev-only** togglable por env var `SERVER_TIMING=1` (default off). Cada estágio reporta nanos:
  - `parse` (`TransactionParser.Parse`)
  - `s1-cent` (centroid scan, 4096 L2 float32)
  - `s1-scan` (cluster scans iniciais, nprobe=35)
  - `s1-bbox` (bbox repair pass)
  - `s2-rerank` (float32 rerank)
- Modificar `scripts/k6/bench.js` para enviar **payloads variados** (10 amount-buckets × 5 mcc × 3 km_from_home → 150 variações) e parsear o header Server-Timing para registrar percentis por estágio.

### Fase 3 — Decidir frente
Com o breakdown na mão:
- **Bbox repair > 30% do tempo** → Caminho 3a: reduzir `numClusters` (4096 → 2048 ou 1024) + ajustar `nprobeFull`. Validar accuracy 100% antes/depois.
- **Centroid scan > 20% do tempo** → quantizar centroids para int16 (hoje são float32) ou hierarquizar (2-level k-means).
- **Variância de runtime > 30%** → Caminho 2: huge pages, async fast-path no Kestrel, GC tuning.
- **Misto** → atacar ambos em paralelo.

### Fase 4 — Otimizações de baixo risco em paralelo
Independentemente do que dominar, aplicar (cada uma em commit separado, com k6 antes/depois):
- Bbox repair: trocar busca linear por bitset (`stackalloc ulong[64]` para 4096 bits).
- Bbox repair: ordenar clusters por lower-bound antes de iterar; parar quando lb > worstDist.
- `madvise(MADV_HUGEPAGE)` no `Prefault()` (`IvfDetector.cs:113-118`).
- Pre-aceitar até 1KB no `pipeReader.TryRead` antes do `await` (`Program.cs:108-109`).

### Fase 5 — Parada
Quando uma iteração render <10% de ganho ou após ~2h de trabalho contínuo. Entregar relatório com:
- Baseline vs final (p50/p95/p99/max/throughput)
- Breakdown por estágio antes/depois
- Lista de mudanças aplicadas, com commit hash
- Mudanças avaliadas mas não aplicadas (com motivo)

## Restrições

- 100% accuracy contra `ExactDetector` é **inviolável**. Validar com `make accuracy ACC_COUNT=10000` em pelo menos 2 seeds após qualquer mudança que toque o caminho de detecção.
- Constraint da rinha mantida (1 CPU / 350MB total).
- Server-Timing fica `default off` no submission — não custa nada quando desligado.
- Sem `perf record` (não disponível no ambiente local).
- Re-preprocessar é caro (~140s/iteração) — minimizar.

## Riscos

- **Build AOT .NET 11 preview**: SDK local é 9.0.202. Build precisa ser feito via Docker (SDK image preview já configurada no `Dockerfile`). Latência de iteração maior.
- **Server-Timing pode adicionar overhead**: timer por request, escrita de header. Mitigado por toggle env var; baseline final é medida com toggle off.
- **Reduzir clusters pode regredir accuracy**: validar com 2 seeds (10k queries cada) antes de commitar a mudança em `Makefile`.
- **Otimizações que melhoram payload-fixo podem regredir payload-variado**: por isso medimos os dois cenários.

## Sucesso

- p99 reduzido em pelo menos uma das medidas (idealmente em ambas).
- Accuracy 100% mantida.
- Mudanças isoladas e revertíveis (pequenos commits, cada um com diff claro).
- Relatório final com números antes/depois para cada mudança.

## Não-objetivo

- Reescrever IVF como HNSW.
- Mudanças que precisem de runtime fora do que já está configurado.
- Tuning de HAProxy (já está em TCP-mode com splice).
- Refatoração ampla do código por estética.
