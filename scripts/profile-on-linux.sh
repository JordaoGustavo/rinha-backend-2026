#!/usr/bin/env bash
# profile-on-linux.sh
#
# Roda toda a captura de profiling num host Linux (WSL2 ou nativo) com PMU
# acessível. Supõe que o repo está clonado e que o k6/Docker estão instalados.
#
# Saída em /tmp/rinha-profile-<timestamp>/ com:
#   - perf-stat.log      (cycles, IPC, cache miss, branch miss, dTLB miss)
#   - perf-script.txt    (sample stack traces; head + tail)
#   - flame.svg          (flame graph dos samples — se inferno-flamegraph existir)
#   - cpu-stat.log       (CFS throttle accounting do cgroup do api1)
#   - k6-varied.log      (per-stage Server-Timing percentis sob carga)
#   - k6-official.log    (run do tester oficial)
#   - results.json       (score do tester oficial)
#   - env.log            (kernel, dotnet, docker, lscpu)
#
# Ao final: gera /tmp/rinha-profile-<timestamp>.tar.gz.
#
# Uso:
#   ./scripts/profile-on-linux.sh
#   ./scripts/profile-on-linux.sh --no-perf   # se PMU bloqueado, pula perf
#   ./scripts/profile-on-linux.sh --quick     # bench 30s em vez de 120s
#

set -euo pipefail

# ─── flags ────────────────────────────────────────────────────────────────────
SKIP_PERF=0
QUICK=0
for arg in "$@"; do
    case "$arg" in
        --no-perf) SKIP_PERF=1 ;;
        --quick)   QUICK=1 ;;
        -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
        *) echo "unknown flag: $arg"; exit 1 ;;
    esac
done

LOAD_DURATION=$([[ $QUICK -eq 1 ]] && echo "30s" || echo "120s")
PERF_RECORD_SEC=$([[ $QUICK -eq 1 ]] && echo "10" || echo "20")
PERF_STAT_SEC=$([[ $QUICK -eq 1 ]] && echo "15" || echo "30")

# ─── paths ────────────────────────────────────────────────────────────────────
TS=$(date +%Y%m%d-%H%M%S)
OUT=/tmp/rinha-profile-${TS}
mkdir -p "$OUT"
cd "$(dirname "$0")/.."
REPO=$(pwd)

echo "==> profile output: $OUT"
echo "==> repo: $REPO"

# ─── sanity ───────────────────────────────────────────────────────────────────
echo "==> environment"
{
    echo "uname: $(uname -a)"
    echo "lscpu:"
    lscpu | head -20 || true
    echo "---"
    echo "docker: $(docker --version 2>/dev/null || echo MISSING)"
    echo "perf:   $(perf --version 2>/dev/null || echo MISSING)"
    echo "k6:     $(docker run --rm grafana/k6 version 2>/dev/null || echo via-docker)"
    echo "perf_event_paranoid: $(cat /proc/sys/kernel/perf_event_paranoid 2>/dev/null || echo unknown)"
    echo "kptr_restrict:       $(cat /proc/sys/kernel/kptr_restrict 2>/dev/null || echo unknown)"
} > "$OUT/env.log"

if ! command -v docker >/dev/null; then
    echo "ERRO: docker não encontrado. Instale antes."
    exit 1
fi

# Relax perf permissions (se possível)
if [[ $SKIP_PERF -eq 0 ]]; then
    if [[ $(cat /proc/sys/kernel/perf_event_paranoid 2>/dev/null || echo 4) -gt 1 ]]; then
        echo "==> tentando relaxar perf_event_paranoid=0 (sudo)"
        sudo sysctl -q kernel.perf_event_paranoid=0 || {
            echo "WARN: não foi possível relaxar paranoid; perf pode falhar"
        }
        sudo sysctl -q kernel.kptr_restrict=0 || true
    fi

    if ! perf list hw 2>/dev/null | grep -q "Hardware event"; then
        echo "WARN: perf hardware events indisponíveis. Continuando sem perf."
        SKIP_PERF=1
    fi
fi

# ─── download fixtures k6 oficial ─────────────────────────────────────────────
if [[ ! -f scripts/k6-official/test/test.js ]]; then
    echo "==> baixando k6 oficial fixtures"
    bash scripts/download-k6-official.sh
fi

# ─── pull e subir stack ───────────────────────────────────────────────────────
echo "==> pull image latest"
docker pull -q ghcr.io/jordaogustavo/rinha-api:latest

echo "==> down + up"
docker compose -f docker/docker-compose.yml --project-directory docker down 2>/dev/null || true
docker compose -f docker/docker-compose.yml --project-directory docker up -d

# Espera ready
echo -n "==> aguardando /ready"
for i in $(seq 1 60); do
    if curl -sf http://localhost:9999/ready >/dev/null 2>&1; then
        echo "  ✓ ($i s)"
        break
    fi
    echo -n "."
    sleep 1
    if [[ $i -eq 60 ]]; then
        echo "  ✗ timeout"
        docker logs docker-api1-1 2>&1 | tail -20
        exit 1
    fi
done

API1_PID=$(docker inspect -f '{{.State.Pid}}' docker-api1-1)
echo "==> docker-api1-1 host pid: $API1_PID"

# ─── carga em background ──────────────────────────────────────────────────────
echo "==> iniciando carga k6-varied ($LOAD_DURATION)"
(docker run --rm --network host -v "$REPO/scripts/k6:/scripts:ro" \
    -e API_URL=http://localhost:9999 -e VUS=20 -e DURATION="$LOAD_DURATION" \
    grafana/k6 run /scripts/bench-varied.js \
    > "$OUT/k6-varied.log" 2>&1) &
K6_PID=$!

# Warmup pra estabilizar branch predictor / icache
echo "==> warmup 20s antes de profilar"
sleep 20

# ─── perf capture ─────────────────────────────────────────────────────────────
if [[ $SKIP_PERF -eq 0 ]]; then
    echo "==> perf stat (${PERF_STAT_SEC}s)"
    sudo perf stat \
        -e cycles,instructions,cache-references,cache-misses,branch-misses,dTLB-load-misses,LLC-load-misses \
        -p "$API1_PID" -- sleep "$PERF_STAT_SEC" \
        2> "$OUT/perf-stat.log" || echo "WARN: perf stat falhou"

    echo "==> perf record (${PERF_RECORD_SEC}s)"
    sudo perf record -o "$OUT/perf.data" -F 999 -g \
        -p "$API1_PID" -- sleep "$PERF_RECORD_SEC" 2>/dev/null \
        || echo "WARN: perf record falhou"

    if [[ -f "$OUT/perf.data" ]]; then
        echo "==> perf script (cortando pra script.txt)"
        sudo perf script -i "$OUT/perf.data" > "$OUT/perf-script-full.txt" 2>/dev/null
        sudo chown "$USER:$USER" "$OUT/perf-script-full.txt" "$OUT/perf.data"
        head -2000 "$OUT/perf-script-full.txt" > "$OUT/perf-script.txt"
        rm -f "$OUT/perf-script-full.txt"

        # Flame graph (se inferno ou FlameGraph estiverem instalados)
        if command -v inferno-flamegraph >/dev/null; then
            echo "==> flame graph via inferno"
            sudo perf script -i "$OUT/perf.data" \
                | inferno-collapse-perf \
                | inferno-flamegraph > "$OUT/flame.svg" \
                || echo "WARN: inferno falhou"
        elif [[ -d /opt/FlameGraph ]]; then
            echo "==> flame graph via Brendan Gregg's FlameGraph"
            sudo perf script -i "$OUT/perf.data" \
                | /opt/FlameGraph/stackcollapse-perf.pl \
                | /opt/FlameGraph/flamegraph.pl > "$OUT/flame.svg" \
                || echo "WARN: FlameGraph falhou"
        else
            echo "INFO: inferno-flamegraph nem /opt/FlameGraph encontrados — pulando SVG"
            echo "      install: cargo install inferno  OR  git clone https://github.com/brendangregg/FlameGraph /opt/FlameGraph"
        fi
    fi
fi

# ─── CFS cgroup stats ─────────────────────────────────────────────────────────
echo "==> cgroup cpu.stat"
{
    for cg in /sys/fs/cgroup/system.slice/docker-*-api1-*.scope/cpu.stat \
              /sys/fs/cgroup/docker/*/cpu.stat; do
        [[ -f "$cg" ]] && echo "--- $cg ---" && cat "$cg"
    done
} > "$OUT/cpu-stat.log" 2>&1 || true

# ─── espera k6-varied terminar ────────────────────────────────────────────────
echo "==> aguardando k6-varied terminar"
wait "$K6_PID" || true

# ─── k6 oficial (mede score) ──────────────────────────────────────────────────
echo "==> rodando k6 oficial (~2 min)"
mkdir -p scripts/k6-official/test/test
docker run --rm --network host \
    -v "$REPO/scripts/k6-official:/scripts:rw" \
    --user "$(id -u):$(id -g)" --workdir /scripts/test \
    grafana/k6 run test.js > "$OUT/k6-official.log" 2>&1 || true
cp scripts/k6-official/test/results.json "$OUT/results.json" 2>/dev/null || true

# ─── extrai resumo ────────────────────────────────────────────────────────────
echo "==> RESUMO"
echo "─────────────────────────────────────────"
echo "k6-varied (per-stage µs read by k6 as ms):"
grep -E 's_(parse|centroid|scan|bbox|rerank|total)_us' "$OUT/k6-varied.log" 2>/dev/null \
    | head -10 || echo "  (sem dados)"
echo
echo "perf stat (cycles/IPC/cache):"
grep -E 'cycles|instructions|cache-(misses|references)|branch-misses|insn per cycle|dTLB|LLC' "$OUT/perf-stat.log" 2>/dev/null \
    | head -15 || echo "  (skip-perf ou perf falhou)"
echo
echo "k6 oficial:"
grep -E 'p99|final_score|http_errors|fraud_count|legit_count' "$OUT/results.json" 2>/dev/null \
    | head -10 || echo "  (results.json não gerado)"
echo
echo "cgroup throttling (procurar nr_throttled > 0):"
grep -E 'nr_throttled|throttled_time' "$OUT/cpu-stat.log" 2>/dev/null | head -5 || echo "  (sem dados)"
echo "─────────────────────────────────────────"

# ─── empacota ─────────────────────────────────────────────────────────────────
TARBALL="/tmp/rinha-profile-${TS}.tar.gz"
tar -czf "$TARBALL" -C /tmp "rinha-profile-${TS}"
echo "==> tudo em: $TARBALL ($(du -h "$TARBALL" | cut -f1))"

# Cleanup containers
echo "==> down stack"
docker compose -f docker/docker-compose.yml --project-directory docker down 2>/dev/null || true

echo "==> done. Arquivos pra colar de volta no chat:"
echo "    cat $OUT/perf-stat.log"
echo "    cat $OUT/k6-varied.log | grep s_"
echo "    cat $OUT/results.json"
echo "    cat $OUT/cpu-stat.log | grep -i throttle"
echo "    head -200 $OUT/perf-script.txt   # se quiser parte do call stack"
