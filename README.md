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
