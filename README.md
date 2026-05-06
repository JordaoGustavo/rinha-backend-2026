# Rinha de Backend 2026 — Fraud Detection API

Submission for [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026): a fraud-detection HTTP API that classifies a transaction by computing a 14-dimensional feature vector and finding its 5 nearest neighbors in a pre-built reference index of 3M labelled vectors.

## Stack

- **C# / .NET 11 (preview)** — AOT compiled, native binary, no JIT, no runtime metadata.
- **Kestrel** (slim) — Unix-socket transport between LB and APIs, no body buffering, no Server header.
- **HAProxy 3.1** — TCP-mode (L4) load balancer, round-robin across two API replicas.
- **Custom IVF index** — int16-quantized centroids + cluster-bucketed vectors, mmap'd from a pre-computed binary file. Distance kernel uses AVX2 (linux-x64) or NEON (linux-arm64) with FMA + scalar fallback.

## Architecture

```
            ┌───────────────────┐
client ───▶ │ haproxy :9999     │ TCP-mode, splice, round-robin
            │ (CFS quota 20%)   │
            └─┬─────────────┬───┘
              │unix-socket  │unix-socket
              ▼             ▼
        ┌─────────┐   ┌─────────┐
        │  api1   │   │  api2   │  Kestrel + AOT, mmap'd index
        │ (40%)   │   │ (40%)   │
        └────┬────┘   └────┬────┘
             └──── shared mmap ───┐
                                  ▼
                          /data/ivf.bin
```

Resource budget under the rinha 1-CPU / 350MB limit:

| service | cpuset | CFS quota | memory |
|--|--|--|--|
| haproxy | core 0    | 20% | 8 MB |
| api1    | cores 1,2 | 40% | 165 MB |
| api2    | cores 2,3 | 40% | 165 MB |
| **total** | — | **100%** | **338 MB** |

## Endpoints

- `GET /ready` — 200 once the index is loaded, prefaulted, and the warm-up KNN pass has completed.
- `POST /fraud-score` — accepts the transaction payload spec'd in the rinha rules; returns `{"approved": bool, "fraud_score": number}`.

The API never returns a 5xx. On any unexpected parse/IO error the handler falls back to `{"approved": true, "fraud_score": 0.0}` — per the scoring weights (`Err=5` vs `FN=3`), a benign 200 always beats a 500.

## Detection algorithm

1. Parse the JSON body with `Utf8JsonReader` directly into a stack-allocated `Span<float>` of 14 features (zero allocation, FNV hash for known field names, ISO-8601 byte parsing).
2. Quantize the query vector to int16 once.
3. Pick `nprobe = 35` closest centroids out of 4096 (int16 SAD pre-filter, then float32 L2).
4. Sweep all vectors in the chosen clusters, keep top-5 by L2 distance.
5. `fraud_score = (#fraud-labelled neighbours) / 5`. Approve when `< 0.6`, deny otherwise.

## Build & run

```bash
make preprocess    # downloads references.json.gz (~48 MB) and builds data/ivf.bin (~140s)
make docker-build
make docker-up
curl http://localhost:9999/ready
```

## Load test

```bash
make k6 K6_VUS=20 K6_DURATION=60s
```

Drives a fixed payload at `/fraud-score` from a containerized k6 and prints latency percentiles + throughput.

## Optimizations

The rinha scoring formula rewards both p99 latency and detection accuracy, so we paid for them on every layer:

### CPU & cgroup tuning

- **CFS quota at 10ms period** (instead of the default 100ms) — when a container exhausts its bandwidth, the worst-case throttle wait drops from ~90ms to ~9ms. Same total bandwidth, much shorter p99 tail.
- **Disjoint cpusets** — haproxy on core 0, api1 on cores 1,2, api2 on cores 2,3. Eliminates L1/L2 cache pollution between the LB and the API hot path.
- **HAProxy in TCP mode (L4)** — splices raw bytes between client and backend, no HTTP parsing on the LB. Per-request cost dropped from ~150 µs (HTTP mode) to ~30 µs.
- **Memory headroom** — 165 MB × 2 + 8 MB = 338 MB, leaves 12 MB under the 350 MB cap to absorb glibc / Kestrel transients without OOM-kill.

### .NET / Kestrel

- **AOT compile** with `IlcOptimizationPreference=Speed`, `IlcInstructionSet=avx2`, `IlcMaxVectorTBitWidth=256`. Native binary, no JIT, no runtime metadata.
- **Trimmed runtime features**: `InvariantGlobalization`, `DebuggerSupport=false`, `EventSourceSupport=false`, `MetricsSupport=false`, `StackTraceSupport=false`, `HttpActivityPropagationSupport=false`, `UseSystemResourceKeys=true`.
- **Server GC, no concurrent GC, no tiered adaptation** — shorter pauses, fewer surprises in steady state.
- **Slim Kestrel**: `AddServerHeader=false`, `MinDataRate=null` (kills the 1 Hz watchdog timer that fires mid-request behind a LB), `discardResponseBodies=true` for /ready.
- **Unix-socket transport** between haproxy and Kestrel — no TCP/loopback overhead on the in-pod hop.
- **`ContentLength` upfront** — Kestrel emits the response in a single fixed-length packet, skipping chunked encoding and the trailing chunk.
- **Sync `BodyWriter.GetSpan` + `Advance`** — no async state machine per request; the pipe flushes when the handler returns.
- **ThreadPool prewarm** (`SetMinThreads(8, 8)`) — avoids the hill-climb ramp-up during the first sustained burst.
- **Index prefault on startup** — touches every 4 KB page of the mmap'd `.bin` once, so the first real request doesn't fault major pages.
- **200-query KNN warmup** — stabilizes branch predictor + microcode caches before serving traffic.
- **12 pre-baked response bodies** — `{approved}×6 + {denied}×6` UTF-8 byte arrays cached at startup; the handler picks one and copies bytes, never allocates a JSON string per request.
- **Catch-all → benign 200** — any unhandled error returns `{approved:true, fraud_score:0.0}`. Per the rinha weights (`Err=5` vs `FN=3` vs `FP=1`), a wrong answer is always cheaper than a 5xx.

### Zero-allocation JSON parser

- **`Utf8JsonReader` over the body bytes** — no string allocations, no `JsonDocument` tree.
- **Field IDs as small ints** — context-aware switch on `ValueTextEquals("field"u8)` instead of comparing strings.
- **MCC table as `float[10000]`** — direct array index (1 branch + 1 load, ~5 ns) replaces `Dictionary<int, float>` (hash + bucket walk + cache miss, ~50–300 ns).
- **FNV-1a 64-bit hash on raw bytes** for `known_merchants` membership — `ulong` stored in a `stackalloc` buffer, no string materialization.
- **ISO-8601 timestamp parsed digit-by-digit from UTF-8** — no `DateTime.Parse`, no culture lookup, no exception path.
- **Output vector** lives in a `stackalloc Span<float>[16]` — quantization, KNN, response build all happen on stack.

### Distance kernel (`SimdDistance.cs`)

- **Multi-ISA dispatch** — at runtime, picks FMA → AVX2 → ARM NEON → SSE → scalar. AOT codegen happily emits the AVX2 path on `linux-x64`.
- **AVX2 + FMA** — `Fma.MultiplyAdd` accumulates the squared diffs in 2 256-bit registers covering 16 dims.
- **ARM NEON path** — widening multiply (`MultiplyWideningLower/Upper`) for int16, ~2.5× faster than scalar on Apple Silicon.
- **Int16 quantization** with `scale = 4096` — 16-dim vector packs in 32 bytes (vs 64 bytes for float32). Fits one AVX2 register; one `MultiplyAddAdjacent` covers 8 dim-pairs.
- **Byte SAD pre-filter** (`Sse2.SumAbsoluteDifferences` / `AdvSimd.AbsoluteDifference`) — coarse pruning before the expensive int32-accumulating L2.
- **Bbox lower bound** per cluster — sum of squared per-dim gaps to the cluster's int16 bbox; if the bound already exceeds the worst-K distance, skip the cluster entirely.
- **Early-exit pre-filter** — partial L2 over the first 8 dims; if the partial already exceeds the worst-K, the full 16-dim distance must too (sum-of-squares is monotonic), so we skip the second half.
- **`Sse.Prefetch0`** on the next-but-one block during cluster scan — keeps the L2 prefetcher fed.

### IVF index

- **K-means in 4096 clusters**, parallel build via `Parallel.For` over 3M vectors.
- **AoSoA-block layout** — vectors stored in 8-vector blocks, dim-pair-interleaved, so one AVX2 `MultiplyAddAdjacent` computes 8 distances in parallel (output = 8 int32 partial distances, one per vector lane).
- **Block padding** — last block padded to 8 with zero rows; the scan loop guards via `validLanes < 8`.
- **Per-cluster int16 bbox** for the lower-bound test (above).
- **Adaptive nprobe**: `nprobeFast=5` on the first pass; if the result is borderline (fraudCount ∈ [2..4]), redo with `nprobeFull=40` + bbox repair. Saves ~70% of work on the easy queries.
- **Top-5 fixed-size max-heap on stack** — `int* heapIdx` and `int* heapDist`, all sift-down / push / replace-top inlined.
- **mmap'd binary** — index loaded `MemoryMappedFileAccess.Read`, OS handles paging; the runtime image bakes the `.bin` in so the container needs no host volumes.
- **Magic-byte dispatch** — `IFraudDetector.Open` reads the first 4 bytes (`IVFR` / `KMKN`) and picks the matching detector. Lets us swap algorithms without changing the API.

### Build & deploy

- **Multi-stage Dockerfile** — SDK image for compile, `runtime-deps` for runtime. Image is fully self-contained: the build stage downloads `references.json.gz` + lookup tables and runs `preprocess` to bake `/data/ivf.bin` in. Reviewers `docker compose up` and it just works.
- **GitHub Actions workflow** publishes `ghcr.io/<owner>/rinha-api` on every push to `main` — free hosting, GHA cache, no extra secrets.

## Lessons learned

A few of the things we discovered the hard way:

1. **HTTP errors are catastrophic in this scoring formula.** `Err=5` weight vs `FN=3` vs `FP=1` means a 5xx is 5× worse than a wrong answer. The first version had a strict parser that threw on edge-case payloads — switching to a benign 200 fallback in the catch-all was the single biggest detection-score win.
2. **HAProxy in HTTP mode was a hidden bottleneck.** Sitting on 20% of one CPU, it spent most of that parsing HTTP headers. TCP mode + splice cut per-request LB cost by 5×; the rest of the budget went back to the API replicas.
3. **CFS period matters more than CFS quota.** Same bandwidth, but going from 100 ms → 10 ms period turns a 90 ms throttle stall (a p99 catastrophe) into a 9 ms blip. This was an unexpected p99 win that you can't see in p50 dashboards.
4. **AOT means no JIT warmup, but cold caches are still real.** First-request latency was ~50× p50 until we added the page prefault + 200-query KNN warmup. Don't trust "AOT = ready instantly".
5. **Int16 quantization is a free 2× speedup for KNN.** Once the score is rounded to 4 decimals (which the rinha reference vectors are anyway), float32 precision past `scale=4096` is wasted. AVX2 holds 8 int16 lanes vs 4 float32, and `MultiplyAddAdjacent` does the squared-diff accumulator in one instruction.
6. **VP-Tree was the wrong shape.** Our first index was a vantage-point tree — recursive, branchy, fundamentally serial per query. It wouldn't vectorize. Switching to IVF (flat clusters, batch-scannable) was the architectural lever that unlocked all the SIMD work that followed.
7. **Adaptive nprobe is the cheapest correctness fix.** Most queries are decisively safe (0–1 fraud neighbors) or decisively risky (5). Spending the full 40-cluster scan on every query is wasteful — only the borderline 2–4 cases need it. Costs nothing on the easy ones, fixes the hard ones.
8. **Memory headroom you don't think you need, you need.** Setting limits to exactly 350 MB (the rule cap) caused intermittent OOM-kills under load — Kestrel and glibc occasionally bump the working set transiently. 12 MB of slack made it stable.
9. **`ValueTextEquals("name"u8)` always beats string comparison.** Idiom worth internalizing — works on the raw UTF-8 bytes of the buffer, no string materialized, no allocator hit.
10. **Server-Timing was useful in dev, dangerous in submission.** We added a `Server-Timing: app;dur=…` header to attribute time between LB hops and server compute during k6 load tests. Removed it for submission — the per-request `Stopwatch.GetElapsedTime` + dictionary write was a few hundred nanoseconds we didn't need to spend in the official run.

## Repository layout

```
src/Api/
  Program.cs                 # Kestrel slim, /ready, /fraud-score
  TransactionParser.cs       # zero-alloc JSON → 14-dim float vector
  SimdDistance.cs            # AVX2 / NEON / FMA / scalar fallback
  FraudDetector/
    IFraudDetector.cs        # magic-byte dispatch (IVFR / KMKN)
    Ivf/                     # IVF index — production path
    Kmknn/                   # alternative index (cluster-pruning KNN)
  Commands/
    PreprocessCommand.cs     # builds .bin from references.json.gz
  Preprocessing/
    IvfBuilder.cs            # parallel k-means, int16 quantization

docker/
  Dockerfile                 # multi-stage AOT, builds .bin during image build
  docker-compose.yml         # haproxy + api1 + api2 (mounts ./data for fast iteration)
  haproxy.cfg                # TCP-mode LB
scripts/
  download-resources.sh      # fetches references / mcc_risk / normalization from upstream
  k6/bench.js                # simple load test
.github/workflows/
  publish-image.yml          # builds & pushes ghcr.io/<owner>/rinha-api on push to main
```

## License

MIT — see [LICENSE](./LICENSE).
