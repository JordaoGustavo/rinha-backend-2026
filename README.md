# Rinha de Backend 2026 — Submission

This branch contains the minimum to run the official rinha test engine:

| file | purpose |
|--|--|
| `docker-compose.yml` | haproxy + 2 API replicas, port 9999, 1 CPU / 338 MB total |
| `haproxy.cfg` | TCP-mode L4 load balancer, round-robin (no fraud logic) |
| `info.json` | participant metadata (rinha submission rule) |
| `LICENSE` | MIT |

The full source code (C# / .NET 11 AOT, IVF KNN over 3M reference vectors) lives on the [`main` branch](https://github.com/JordaoGustavo/rinha-backend-2026/tree/main).

## Stack

- **C# / .NET 11 (preview, AOT-compiled)** — native binary, no JIT, no runtime metadata.
- **Kestrel slim** — Unix-socket transport between haproxy and APIs.
- **HAProxy 3.1 (TCP mode)** — splices raw bytes between client and backend, ~30 µs per request.
- **Custom IVF index** — int16-quantized centroids + cluster-bucketed vectors, mmap'd from a binary file built into the runtime image during `docker build`.

## Resource budget

| service | cpuset | CFS quota | memory |
|--|--|--|--|
| haproxy | core 0    | 20% | 8 MB |
| api1    | cores 1,2 | 40% | 165 MB |
| api2    | cores 2,3 | 40% | 165 MB |
| **total** | — | **100%** | **338 MB** |

## How to run (test engine perspective)

```bash
docker compose up -d
curl -sf http://localhost:9999/ready
```

The image is fully self-contained: the build stage downloads `references.json.gz` + the rinha lookup tables and pre-builds the IVF index, so the runtime container needs no host volumes.

## License

MIT — see [LICENSE](./LICENSE).
