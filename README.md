# Rinha de Backend 2026

Fraud-detection API for the [Rinha de Backend 2026](https://github.com/zanfranceschi/rinha-de-backend-2026). Each request maps a transaction to a 14-dim feature vector, finds its 5 nearest neighbours among 3M reference vectors, and approves when fewer than 3 of the 5 are labelled fraud.

The deploy manifest used by the official test engine lives on the [`submission`](https://github.com/JordaoGustavo/rinha-backend-2026/tree/submission) branch.

## Stack

- C# / .NET 11 (preview), AOT-compiled.
- Kestrel slim, Unix-socket transport.
- HAProxy 3.1 in TCP (L4) mode, round-robin.
- Custom IVF index (int16 quantized, 4096 clusters) with float32 rerank, mmap'd.

## Resource budget (submission)

| service | cpuset | cpus | memory |
|---|---|---|---|
| haproxy | 0     | 0.20 | 16 MB  |
| api1    | 1,2   | 0.40 | 162 MB |
| api2    | 2,3   | 0.40 | 162 MB |
| total   |       | 1.00 | 340 MB |

Default `cpu_period` (100 ms) per the rinha 2026 template. The handler runs in <300 µs steady-state, so most requests stay off the CFS-throttle path entirely.

## Endpoints

- `GET /ready` — 200 once the index is mmap'd, prefaulted, and the 200-query warmup completed.
- `POST /fraud-score` — accepts the rinha payload, returns `{"approved": bool, "fraud_score": float}`. Any thrown exception falls through to a benign 200 (`Err=5` weight vs `FN=3` weight makes a wrong answer cheaper than a 5xx).

## Algorithm

1. Parse the JSON body into a `Span<float>[14]`.
2. Quantize to int16 (`scale = 4096`).
3. **Stage 1, IVF search.** Heap-pick `NPROBE_FULL=5` closest centroids out of 4096, scan those clusters, then a bbox-repair pass over the remaining 4091 (skips a cluster when its bbox lower-bound > current worst-K). Keeps top-6 candidates by int16 L2.
4. **Stage 2, float32 rerank.** Look each of the 6 candidates up in `exact.bin` via `OriginalIndices[slot]`, compute float32 L2, keep top-5 via insertion-sort.
5. `fraud_count = sum(labels[topK])`. Approve if `fraud_count < 3`.

The IVF + exact-rerank combo agrees with the brute-force float32 oracle on 20 000 synthetic queries across two seeds (`make accuracy`).

## What each component does

### `src/Api/Api.csproj` — AOT build

- `PublishAot=true`, `IlcOptimizationPreference=Speed`, `IlcInstructionSet=avx2`, `IlcMaxVectorTBitWidth=256`.
- `ServerGC=true`, concurrent GC off, tiered compilation off, `DynamicAdaptationMode=0`.
- Trimmed runtime: `InvariantGlobalization`, `DebuggerSupport=false`, `EventSource=false`, `Metrics=false`, `StackTrace=false`, `HttpActivityPropagation=false`, `UseSystemResourceKeys=true`.

### `src/Api/Program.cs` — Kestrel host

- Slim builder, `AddServerHeader=false`, `MinDataRate=null` (kills the 1 Hz watchdog that fires mid-request behind a LB).
- Listens on `SOCKET_PATH` (Unix socket), `chmod 0777` after start.
- Body read fast-path: 8 attempts of `pipeReader.TryRead()` with `SpinWait.SpinOnce()` before falling into `ReadAtLeastAsync`. Body usually arrives within microseconds via haproxy splice; the await would otherwise add ~1–3 ms to p99.
- `ResponseCache`: 12 pre-baked UTF-8 byte arrays (`approved × 6 fraud counts + denied × 6`). Handler copies bytes, never allocates JSON.
- 200-query KNN warmup before serving traffic.
- `try / catch` around the full handler: any exception → 200 with the fallback body. Never 5xx.
- No `SetMinThreads` (tested and dropped: at 0.4 effective CPU the cross-worker handoff dominated p99).

### `src/Api/TransactionParser.cs` — zero-alloc JSON → 14-dim vector

- `Utf8JsonReader` over the body, no `JsonDocument`.
- Field dispatch: small int IDs, picked via `reader.ValueTextEquals("name"u8)` per nested context (`transaction`, `customer`, `merchant`, `terminal`, `last_transaction`).
- MCC risk lookup as `float[10000]` (1 branch + 1 load) instead of a `Dictionary<int, float>`.
- ISO-8601 timestamps parsed digit-by-digit from the UTF-8 span (no `DateTime.Parse`, no culture lookup).
- `known_merchants` membership: FNV-1a 64-bit hashes in a `stackalloc ulong[32]`, compared against the `merchant.id` hash. No string materialisation.

### `src/Api/SimdDistance.cs` — distance kernels

- `EuclideanSquaredPtr(float*, float*)`: runtime dispatch FMA → AVX → NEON → SSE → scalar. Used in the float32 rerank.
- `Int16L2Squared(short*, short*)`: AVX2 `MultiplyAddAdjacent` over a 16-lane `Vector256<short>` (whole vector in one SIMD op). NEON path uses `MultiplyWideningLower/Upper` for the int16-to-int32 widening. SSE2 + scalar fallbacks.
- `Int16BboxLowerBound(q, min, max)`: per-dim gap = `max(min-q, 0) + max(q-max, 0)`, then L2 of the gap vector. AVX2 / NEON / scalar.
- `ByteSAD`: 16-byte SAD via `Sse2.SumAbsoluteDifferences` / `AdvSimd.AbsoluteDifference`.

### `src/Api/FraudDetector/Ivf/IvfDetector.cs` — search path

- mmap of `ivf.bin` (read-only) plus optional mmap of `exact.bin` for float32 rerank.
- `Prefault()`: walks every 4 KB page once. Issues `madvise(HUGEPAGE)` + `WILLNEED` on `ivf.bin`, `HUGEPAGE` + `RANDOM` on `exact.bin` (rerank touches ~6 random vectors per query, sequential readahead would waste bandwidth).
- Stage 1 (`FindKNearest`):
  - Centroid scan over all 4096 with `Int16L2Squared`, kept in a max-heap of size `NPROBE_FULL`.
  - Cluster scan: when the K-heap is full, skip via `Int16BboxLowerBound` before paying the full block scan.
  - Bbox-repair: bitset (`ulong[(numClusters+63)/64]`) of already-scanned clusters, then iterate the rest and scan only when `lb ≤ heapDist[0]`.
- `ScanClusterAvx2`: vectors stored in 8-vector AoSoA blocks. One `MultiplyAddAdjacent` over 4 dim-pairs (8 dims) gives a partial L2 across all 8 lanes; if the partial already exceeds the worst-K threshold for every lane, the second half is skipped. `Sse.Prefetch0` on the next-but-one block keeps the L2 prefetcher fed.
- Stage-1 K-heap: 2 stack arrays (`int* heapIdx`, `int* heapDist`), push / replace-top / sift inlined.
- Stage 2 (`ScoreCore`): for each of the 6 candidates, look up `OriginalIndices[slot]` and read the float32 vector from `exact.bin`; insertion-sort into a 5-slot stack array (cheaper than max-heap at K=5).
- `NPROBE_FAST` and `NPROBE_FULL` overridable via env (binary header carries defaults).

### `src/Api/FraudDetector/Exact/ExactDetector.cs`

- Flat float32 mmap of all 3M vectors. Used as the rerank source by IVF and as the brute-force oracle by `make accuracy`.

### `src/Api/Preprocessing/IvfBuilder.cs`

- K-means at K=4096, 20 iterations, parallel assignment over 3M (`Parallel.For`).
- Reorders vectors so each cluster's slots are contiguous, records `OriginalIndices[slot] → globalIndex` so the runtime can pull the exact float32 vector from `exact.bin` during rerank.

### `src/Api/Hint/MmapHints.cs`

- Thin `madvise` P/Invoke (`libc`). No-op on non-Linux.
- `MADV_HUGEPAGE`: backs the index range with 2 MB pages where the kernel can. The 110 MB IVF index goes from ~28 k 4 KB pages to ~55 2 MB pages — fits in the Haswell L1 dTLB.
- `MADV_WILLNEED`: kicks readahead so the first real query doesn't fault.
- `MADV_RANDOM`: disables sequential readahead for `exact.bin`.

### `docker/Dockerfile`

- Multi-stage: SDK image to download `references.json.gz` + lookup tables, run `preprocess` (builds `/data/ivf.bin` and `/data/exact.bin`), then AOT publish. `runtime-deps` final stage. Both `.bin` files are copied into the final image — no host volumes needed.
- Image published to `ghcr.io/jordaogustavo/rinha-api` by the GHA workflow on push to `main`.

### `submission/haproxy.cfg`

- `mode tcp`, `nbthread 1`, `splice-auto`, `option tcpka`, `no log`.
- `timeout connect 200ms` (50 ms was TCP-RST'ing connections during CFS throttle bursts on the submission Mac mini).
- `maxconn 20000`, round-robin over `unix@/run/sock/api{1,2}.sock`.

### `submission/docker-compose.yml`

- Three services on a bridge network. CPU limits via `deploy.resources.limits.cpus` (rinha 2026 standard template).
- `tmpfs` volume for the haproxy↔API Unix socket directory.
- `NPROBE_FULL=5` env on each API: 100% accuracy across `nprobe ∈ [3, 8]`, 5 is the latency floor under bbox-repair + lb-skip.

## Run locally

```bash
make preprocess         # downloads references.json.gz, builds data/ivf.bin
make preprocess-exact   # builds data/exact.bin (oracle / rerank source)
make docker-build
make docker-up
curl http://localhost:9999/ready
```

```bash
make accuracy                              # 10k synthetic queries, IVF vs Exact oracle
make k6 K6_VUS=20 K6_DURATION=60s          # latency / throughput
```

## License

MIT, see [LICENSE](./LICENSE).
