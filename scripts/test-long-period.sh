#!/bin/bash
# Diagnostic: run the test suite against the multi-instance setup with
# cpu_period=100ms (kernel default). If P99 stays around 10ms, CFS throttling
# isn't the dominant tail. If it jumps to ~90ms, throttling IS the issue.
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$SCRIPT_DIR/.."

FORMAT="${FORMAT:-ivf}"
CLUSTERS="${CLUSTERS:-0}"
NPROBE="${NPROBE:-35}"
LABEL="${LABEL:-ivf-0}"
TEST_DATA="${TEST_DATA:-}"
API_URL="http://localhost:9999"
COMPOSE="docker compose -f docker/docker-compose.long-period.yml --project-directory docker"

cleanup() {
    cd "$PROJECT_ROOT"
    $COMPOSE down 2>/dev/null || true
}
trap cleanup EXIT

print_separator() { echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"; }

wait_for_ready() {
    for i in $(seq 1 180); do
        if curl -sf "$API_URL/ready" > /dev/null 2>&1; then
            echo "  API ready after ${i}s"
            return 0
        fi
        sleep 2
    done
    return 1
}

echo ""
print_separator
echo "  CPU PERIOD DIAGNOSTIC (cpu_period=100ms — kernel default)"
echo "  Same total bandwidth (1.0 CPU), wider throttle window"
echo "  Hypothesis: if P99 ≈ 90ms here, CFS throttling is the tail driver"
print_separator
echo ""

if ! docker image inspect "rinha/api:$LABEL" > /dev/null 2>&1; then
    echo "  ERROR: image rinha/api:$LABEL not found. Run 'make test-matrix' first to build it."
    exit 1
fi

docker tag "rinha/api:$LABEL" rinha/api:latest
echo "  Reusing image rinha/api:$LABEL"

echo "  Starting services with cpu_period=100ms..."
cd "$PROJECT_ROOT"
$COMPOSE down 2>/dev/null || true
$COMPOSE up -d

if ! wait_for_ready; then
    echo "  TIMEOUT"
    exit 1
fi

if [ -n "$TEST_DATA" ] && [ -f "$TEST_DATA" ]; then
    cd "$PROJECT_ROOT"
    dotnet run --project src/Api/Api.csproj -c Release -- test "$API_URL" "$TEST_DATA"
else
    echo "  Set TEST_DATA=/path/to/test-data.json to run full suite"
fi

echo ""
echo "  Stopping services..."
$COMPOSE down
