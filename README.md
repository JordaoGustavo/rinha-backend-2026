# Rinha de Backend 2026: fraud-detection API

Submission for [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026). The service classifies a transaction by computing a 14-dimensional feature vector and finding its 5 nearest neighbours in a pre-built reference index of 3M labelled vectors.

## Stack

- **C# / .NET 11 (preview)**, AOT compiled. Native binary, no JIT, no runtime metadata.
- **Kestrel** (slim) over a Unix socket between LB and APIs. No body buffering, no Server header.
- **HAProxy 3.1** in TCP mode (L4), round-robin across two API replicas.
- **Custom IVF index**: int16-quantized centroids and cluster-bucketed vectors, mmap'd from a pre-computed binary. The distance kernel runs AVX2 on linux-x64, NEON on linux-arm64, with a scalar fallback.

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

Resource budget under the 1-CPU / 350MB limit:

| service | cpuset | CFS quota | memory |
|--|--|--|--|
| haproxy | core 0    | 20% | 8 MB |
| api1    | cores 1,2 | 40% | 165 MB |
| api2    | cores 2,3 | 40% | 165 MB |
| **total** |       | **100%** | **338 MB** |

## Endpoints

- `GET /ready`: 200 once the index is loaded, prefaulted, and the warm-up KNN pass has completed.
- `POST /fraud-score`: accepts the transaction payload spec'd in the rinha rules. Returns `{"approved": bool, "fraud_score": number}`.

The API never returns a 5xx. On any unexpected parse or IO error the handler falls back to `{"approved": true, "fraud_score": 0.0}`. Per the scoring weights (`Err=5` vs `FN=3`), a benign 200 always beats a 500.

## Detection algorithm

1. Parse the JSON body with `Utf8JsonReader` directly into a stack-allocated `Span<float>` of 14 features. Zero allocation, FNV hash for known field names, ISO-8601 byte parsing.
2. Quantize the query vector to int16 once.
3. Pick `nprobe = 35` closest centroids out of 4096 (int16 SAD pre-filter, then float32 L2).
4. Sweep all vectors in the chosen clusters, keep top-5 by L2 distance.
5. `fraud_score = (#fraud-labelled neighbours) / 5`. Approve when `< 0.6`, deny otherwise.

## Build & run

```bash
make preprocess         # downloads references.json.gz (~48 MB) and builds data/ivf.bin (~140s)
make preprocess-exact   # builds the float32 oracle data/exact.bin (~3s)
make docker-build
make docker-up
curl http://localhost:9999/ready
```

## Accuracy harness

```bash
make accuracy                              # 10 000 synthetic queries, default seed
make accuracy ACC_SEED=42                  # different seed
make accuracy ACC_COUNT=100000 ACC_SEED=7  # stress the tail
```

Generates random 14-d queries and compares the production `IvfDetector` against the brute-force `ExactDetector` oracle. Reports approval-level disagreements, false-positive vs false-negative breakdown, fraud-count mismatches, and per-query latencies for both. Exits non-zero on any disagreement.

## Load test

```bash
make k6 K6_VUS=20 K6_DURATION=60s
```

Drives a fixed payload at `/fraud-score` from a containerized k6 and prints latency percentiles plus throughput.

## Optimizations

The rinha scoring formula rewards both p99 latency and detection accuracy, so most layers got tuned for one or the other.

### CPU & cgroup

- CFS quota at 10ms period instead of the default 100ms. When a container exhausts its bandwidth, the worst-case throttle wait drops from ~90ms to ~9ms. Same total bandwidth, shorter p99 tail.
- Disjoint cpusets: haproxy on core 0, api1 on cores 1,2, api2 on cores 2,3. Removes L1/L2 cache pollution between the LB and the API hot path.
- HAProxy in TCP mode (L4) splices raw bytes between client and backend, no HTTP parsing on the LB. Per-request cost dropped to ~30 µs from ~150 µs in HTTP mode.
- 165 MB × 2 + 8 MB = 338 MB, leaving 12 MB under the 350 MB cap to absorb glibc and Kestrel transients without OOM-kills.

### .NET / Kestrel

- AOT compile with `IlcOptimizationPreference=Speed`, `IlcInstructionSet=avx2`, `IlcMaxVectorTBitWidth=256`.
- Trimmed runtime features: `InvariantGlobalization`, `DebuggerSupport=false`, `EventSourceSupport=false`, `MetricsSupport=false`, `StackTraceSupport=false`, `HttpActivityPropagationSupport=false`, `UseSystemResourceKeys=true`.
- Server GC, no concurrent GC, no tiered adaptation. Shorter pauses, fewer surprises in steady state.
- Slim Kestrel: `AddServerHeader=false`, `MinDataRate=null` (kills the 1 Hz watchdog timer that fires mid-request behind a LB), `discardResponseBodies=true` for `/ready`.
- Unix-socket transport between haproxy and Kestrel, no TCP/loopback overhead on the in-pod hop.
- `ContentLength` set upfront so Kestrel emits the response in a single fixed-length packet, skipping chunked encoding and the trailing chunk.
- Sync `BodyWriter.GetSpan` + `Advance`. No async state machine per request; the pipe flushes when the handler returns.
- ThreadPool prewarm (`SetMinThreads(8, 8)`) avoids the hill-climb ramp during the first sustained burst.
- Index prefault on startup touches every 4 KB page of the mmap'd `.bin` once, so the first real request doesn't fault major pages.
- 200-query KNN warmup stabilises branch predictor and microcode caches before serving traffic.
- 12 pre-baked response bodies (`{approved}×6 + {denied}×6` UTF-8 byte arrays) cached at startup. The handler picks one and copies bytes, never allocating a JSON string per request.
- Catch-all returns `{approved:true, fraud_score:0.0}`. Per the rinha weights (`Err=5` vs `FN=3` vs `FP=1`), a wrong answer is always cheaper than a 5xx.

### Zero-allocation JSON parser

- `Utf8JsonReader` over the body bytes, no string allocations and no `JsonDocument` tree.
- Field IDs as small ints, switched on `ValueTextEquals("field"u8)` instead of comparing strings.
- MCC table as `float[10000]`. Direct array index (1 branch + 1 load, ~5 ns) replaces `Dictionary<int, float>` (hash + bucket walk + cache miss, ~50–300 ns).
- FNV-1a 64-bit hash on the raw bytes for `known_merchants` membership: `ulong` stored in a `stackalloc` buffer, no string materialisation.
- ISO-8601 timestamps parsed digit-by-digit from UTF-8. No `DateTime.Parse`, no culture lookup, no exception path.
- The output vector lives in a `stackalloc Span<float>[16]`. Quantization, KNN, and response build all happen on stack.

### Distance kernel (`SimdDistance.cs`)

- Multi-ISA dispatch at runtime: FMA → AVX2 → ARM NEON → SSE → scalar. AOT codegen emits the AVX2 path on linux-x64.
- AVX2 + FMA: `Fma.MultiplyAdd` accumulates squared diffs in two 256-bit registers covering 16 dims.
- ARM NEON path uses widening multiply (`MultiplyWideningLower/Upper`) for int16, ~2.5× scalar on Apple Silicon.
- Int16 quantization with `scale = 4096`. A 16-dim vector packs in 32 bytes (vs 64 bytes for float32) and fits one AVX2 register; one `MultiplyAddAdjacent` covers 8 dim-pairs.
- Byte SAD pre-filter (`Sse2.SumAbsoluteDifferences` / `AdvSimd.AbsoluteDifference`) for coarse pruning before the int32-accumulating L2.
- Bbox lower bound per cluster: sum of squared per-dim gaps to the cluster's int16 bbox. If the bound already exceeds the worst-K distance, skip the cluster.
- Early-exit pre-filter via partial L2 over the first 8 dims. Sum-of-squares is monotonic, so if the partial already exceeds the worst-K, the full 16-dim distance must too.
- `Sse.Prefetch0` on the next-but-one block during the cluster scan keeps the L2 prefetcher fed.

### IVF index

- K-means in 4096 clusters, parallel build via `Parallel.For` over 3M vectors.
- AoSoA-block layout: vectors stored in 8-vector blocks, dim-pair-interleaved, so one AVX2 `MultiplyAddAdjacent` computes 8 distances in parallel (output: 8 int32 partial distances, one per lane).
- Block padding: the last block is padded to 8 with zero rows. The scan loop guards via `validLanes < 8`.
- Per-cluster int16 bbox for the lower-bound test above.
- Two-stage scan with float32 rerank. Stage 1 runs a full int16 search over all 4096 clusters with bbox lower-bound pruning and keeps the top-32 candidates. Stage 2 looks each candidate's *original* index up in a parallel `exact.bin` (flat float32 mirror of `references.json.gz`) and reranks with float32 L2. 0 disagreements with the brute-force oracle across 20 000 synthetic queries on two independent seeds.
- `OriginalIndices` slot table. At preprocess time, alongside the int16 vectors and labels, we store the original index in `references.json.gz` per slot (`-1` for padding). At query time the IVF detector uses this to fetch the exact float32 reference for rerank, instead of dequantizing the int16 (which loses ~1.2·10⁻⁴ per dim).
- Top-5 fixed-size max-heap on stack: `int* heapIdx` + `int* heapDist`, all sift-down / push / replace-top inlined.
- Both `ivf.bin` and `exact.bin` are mmap'd `MemoryMappedFileAccess.Read`, so the OS handles paging. The runtime image bakes both files in, so the container needs no host volumes.
- Magic-byte dispatch: `IFraudDetector.Open` reads the first 4 bytes (`IVFR` / `KMKN` / `EXCT`) and picks the matching detector. Lets us swap algorithms without changing the API. The HTTP server uses `IvfDetector.OpenWithExactRerank` when both files are available.

### Build & deploy

- Multi-stage Dockerfile: SDK image for compile, `runtime-deps` for runtime. The build stage downloads `references.json.gz` and lookup tables, runs `preprocess`, and bakes `/data/ivf.bin` into the final image.
- GitHub Actions workflow publishes `ghcr.io/<owner>/rinha-api` on every push to `main`.

## Lessons learned

A few of the things we found out the hard way:

1. HTTP errors are catastrophic in this scoring formula. `Err=5` vs `FN=3` vs `FP=1` means a 5xx is 5× worse than a wrong answer. The first version had a strict parser that threw on edge-case payloads; switching to a benign 200 fallback in the catch-all was the biggest detection-score win.
2. HAProxy in HTTP mode was a hidden bottleneck. On 20% of one CPU it spent most of that parsing HTTP headers. TCP mode + splice cut per-request LB cost by 5×; the rest of the budget went back to the API replicas.
3. CFS period matters more than CFS quota. Same bandwidth, but going from 100 ms to 10 ms period turns a 90 ms throttle stall (a p99 catastrophe) into a 9 ms blip. You don't see it in p50 dashboards.
4. AOT means no JIT warmup, but cold caches are still real. First-request latency was ~50× p50 until we added the page prefault and 200-query KNN warmup.
5. Int16 quantization is a 2× speedup for KNN. Once the score is rounded to 4 decimals (which the rinha reference vectors are anyway), float32 precision past `scale=4096` is wasted. AVX2 holds 8 int16 lanes vs 4 float32, and `MultiplyAddAdjacent` does the squared-diff accumulator in one instruction.
6. VP-Tree was the wrong shape. Our first index was a vantage-point tree: recursive, branchy, serial per query. It wouldn't vectorize. Switching to IVF (flat clusters, batch-scannable) was the architectural lever that unlocked the SIMD work that followed.
7. Adaptive `nprobeFast` cost us correctness without buying meaningful latency. We were skipping the full pass whenever the fast pass returned 0, 1, or 5 frauds, but `nprobe=5/4096` is far from exhaustive, so it could (and did) misreport a clear case as a clear case. Replacing it with one always-on full pass at `nprobeFull=40` + bbox repair, plus a 32-candidate rerank, took us from 281 disagreements / 10 000 to 0.
8. Dequantizing int16 is not the same as the original float32. Our first 100% attempt reranked top-32 by unpacking the int16 stored vectors back to float32. With `scale=4096`, that round trip loses ~1.2·10⁻⁴ of precision per dim, enough to flip the rank of vectors near the K-th boundary. Storing the original global index in `ivf.bin` and looking the exact float32 reference up in a parallel `exact.bin` mmap closed the gap to zero.
9. Memory headroom you don't think you need, you need. Setting limits to exactly 350 MB caused intermittent OOM-kills under load: Kestrel and glibc occasionally bump the working set transiently. 12 MB of slack made it stable.
10. `ValueTextEquals("name"u8)` always beats string comparison. Works on the raw UTF-8 bytes of the buffer, no string materialised, no allocator hit.
11. Server-Timing was useful in dev, dangerous in submission. We added a `Server-Timing: app;dur=…` header to attribute time between LB hops and server compute during k6 load tests. Removed it for submission: the per-request `Stopwatch.GetElapsedTime` + dictionary write was a few hundred nanoseconds we didn't need to spend in the official run.
12. A correctness oracle pays for itself. Building a float32 brute-force `ExactDetector` and an `accuracy` subcommand that diffs IVF vs oracle on synthetic queries took ~150 lines of code. It found the two precision bugs above (adaptive trigger and int16 dequant rerank) within minutes of being wired up. Both would have shown up as silent score drops in the official run with no actionable signal.
13. HAProxy in TCP mode is hard to beat with a custom proxy. We wrote `lb/lb.c`, a minimal C round-robin TCP→UDS proxy using `epoll` and `splice(2)`. It works, but A/B testing against `haproxy:3.1-alpine` showed identical p99 (both bottlenecked by API CFS-quota throttling, not LB CPU) and the custom proxy lost ~10% throughput. HAProxy 3.1 is well-tuned enough (splice-auto, single-thread fast path, batched epoll) that there's no slack left for a hand-rolled proxy to find. The C source is kept in `lb/` as a record of the experiment.

## Repository layout

```
src/Api/
  Program.cs                 # Kestrel slim, /ready, /fraud-score
  TransactionParser.cs       # zero-alloc JSON → 14-dim float vector
  SimdDistance.cs            # AVX2 / NEON / FMA / scalar fallback
  FraudDetector/
    IFraudDetector.cs        # magic-byte dispatch (IVFR / KMKN / EXCT)
    Ivf/                     # IVF index, production path (int16 + float32 rerank)
    Kmknn/                   # alternative index (cluster-pruning KNN)
    Exact/                   # flat float32 brute-force, used as rerank oracle
  Commands/
    PreprocessCommand.cs     # builds .bin (ivf | kmknn | exact) from references.json.gz
    AccuracyCommand.cs       # offline: synthetic queries, IVF vs ExactDetector mismatch report
  Preprocessing/
    IvfBuilder.cs            # parallel k-means, int16 quantization, original-index map

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

MIT, see [LICENSE](./LICENSE).
