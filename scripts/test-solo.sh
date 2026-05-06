#!/bin/bash
# Single-instance experiment runner. Mirrors test-matrix.sh but uses
# docker-compose.solo.yml (1 API, no HAProxy) and only one config.
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$SCRIPT_DIR/.."

FORMAT="${FORMAT:-ivf}"
CLUSTERS="${CLUSTERS:-0}"
NPROBE="${NPROBE:-35}"
LABEL="solo-${FORMAT}-${CLUSTERS}"
SKIP_BUILD="${SKIP_BUILD:-0}"
TEST_DATA="${TEST_DATA:-}"
API_URL="http://localhost:9999"
COMPOSE="docker compose -f docker/docker-compose.solo.yml --project-directory docker"

ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ] || [ "$ARCH" = "aarch64" ]; then
    RUNTIME_ID="linux-arm64"
    PLATFORM_FLAG=""
else
    RUNTIME_ID="linux-x64"
    PLATFORM_FLAG="--platform linux/amd64"
fi

cleanup() {
    cd "$PROJECT_ROOT"
    $COMPOSE down 2>/dev/null || true
}
trap cleanup EXIT

print_separator() { echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"; }

wait_for_ready() {
    local max_wait=${1:-180}
    for i in $(seq 1 "$max_wait"); do
        if curl -sf "$API_URL/ready" > /dev/null 2>&1; then
            echo "  API ready after ${i}s"
            return 0
        fi
        sleep 2
    done
    echo "  TIMEOUT: API not ready after ${max_wait}s"
    return 1
}

echo ""
print_separator
echo "  SINGLE-INSTANCE EXPERIMENT (no HAProxy, 1.0 CPU)"
echo "  Config: format=$FORMAT clusters=$CLUSTERS nprobe=$NPROBE"
[ "$SKIP_BUILD" = "1" ] && echo "  Build: SKIP (reusing rinha/api:$LABEL)"
[ -n "$TEST_DATA" ] && echo "  Test data: $TEST_DATA"
print_separator
echo ""

if [ "$SKIP_BUILD" = "1" ]; then
    if ! docker image inspect "rinha/api:$LABEL" > /dev/null 2>&1; then
        echo "  ERROR: image rinha/api:$LABEL not found. Run without SKIP_BUILD first."
        exit 1
    fi
    docker tag "rinha/api:$LABEL" rinha/api:latest
else
    echo "  Building Docker image..."
    cd "$PROJECT_ROOT"
    docker build $PLATFORM_FLAG \
        --build-arg RUNTIME_ID="$RUNTIME_ID" \
        --build-arg INDEX_FORMAT="$FORMAT" \
        --build-arg INDEX_CLUSTERS="$CLUSTERS" \
        --build-arg INDEX_NPROBE="$NPROBE" \
        -f docker/Dockerfile -t rinha/api:latest . 2>&1 | tail -5
    docker tag rinha/api:latest "rinha/api:$LABEL"
fi

echo "  Starting service..."
$COMPOSE down 2>/dev/null || true
$COMPOSE up -d

if ! wait_for_ready 180; then
    exit 1
fi

if [ -n "$TEST_DATA" ] && [ -f "$TEST_DATA" ]; then
    echo "  Running full test suite..."
    cd "$PROJECT_ROOT"
    dotnet run --project src/Api/Api.csproj -c Release -- test "$API_URL" "$TEST_DATA"
else
    echo "  Smoke test:"
    curl -sf -X POST "$API_URL/fraud-score" \
        -H 'Content-Type: application/json' \
        -d '{
            "id": "tx-solo-1",
            "transaction": { "amount": 41.12, "installments": 2, "requested_at": "2026-03-11T18:45:53Z" },
            "customer": { "avg_amount": 82.24, "tx_count_24h": 3, "known_merchants": ["MERC-003", "MERC-016"] },
            "merchant": { "id": "MERC-016", "mcc": "5411", "avg_amount": 60.25 },
            "terminal": { "is_online": false, "card_present": true, "km_from_home": 29.23 },
            "last_transaction": null
        }'
    echo ""
fi

echo ""
echo "  Stopping service..."
$COMPOSE down
