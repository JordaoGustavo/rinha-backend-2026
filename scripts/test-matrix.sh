#!/bin/bash
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$SCRIPT_DIR/.."

# ── Configuration matrix ─────────────────────────────────────────────
# Each line: FORMAT CLUSTERS NPROBE LABEL
# CLUSTERS=0 means sqrt(n) default
CONFIGS=(
    "ivf    0      35  ivf-0"
    "kmknn  5000   0   kmknn-5k"
)

# Override matrix via env: MATRIX="ivf:0:35 kmknn:2000:0"
if [ -n "$MATRIX" ]; then
    CONFIGS=()
    for entry in $MATRIX; do
        IFS=: read -r fmt clusters nprobe <<< "$entry"
        CONFIGS+=("$fmt $clusters $nprobe ${fmt}-${clusters}")
    done
fi

SKIP_BUILD="${SKIP_BUILD:-0}"
TEST_DATA="${TEST_DATA:-}"
API_URL="http://localhost:9999"
COMPOSE="docker compose -f docker/docker-compose.yml --project-directory docker"

ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ] || [ "$ARCH" = "aarch64" ]; then
    RUNTIME_ID="linux-arm64"
    PLATFORM_FLAG=""
else
    RUNTIME_ID="linux-x64"
    PLATFORM_FLAG="--platform linux/amd64"
fi

# ── Results table ────────────────────────────────────────────────────
declare -a RESULTS

cleanup() {
    cd "$PROJECT_ROOT"
    $COMPOSE down 2>/dev/null || true
}
trap cleanup EXIT

print_separator() {
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
}

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

smoke_test() {
    local response
    response=$(curl -sf -X POST "$API_URL/fraud-score" \
        -H 'Content-Type: application/json' \
        -d '{
            "id": "tx-matrix-1",
            "transaction": { "amount": 41.12, "installments": 2, "requested_at": "2026-03-11T18:45:53Z" },
            "customer": { "avg_amount": 82.24, "tx_count_24h": 3, "known_merchants": ["MERC-003", "MERC-016"] },
            "merchant": { "id": "MERC-016", "mcc": "5411", "avg_amount": 60.25 },
            "terminal": { "is_online": false, "card_present": true, "km_from_home": 29.23 },
            "last_transaction": null
        }')

    echo "  Response: $response"
    echo "$response" | grep -q '"approved"' && echo "$response" | grep -q '"fraud_score"'
}

run_full_test() {
    local label=$1
    echo "  Running full test suite..."
    cd "$PROJECT_ROOT"
    local output
    output=$(dotnet run --project src/Api/Api.csproj -c Release -- test "$API_URL" "$TEST_DATA" 2>&1) || true
    echo "$output"

    local score_line
    score_line=$(echo "$output" | grep "TOTAL SCORE:" | tail -1)
    if [ -n "$score_line" ]; then
        echo "$score_line"
    fi
}

# ── Run matrix ───────────────────────────────────────────────────────
echo ""
print_separator
echo "  INTEGRATION TEST MATRIX"
echo "  Configs: ${#CONFIGS[@]}"
[ "$SKIP_BUILD" = "1" ] && echo "  Build: SKIP (reusing rinha/api:<label> images)"
[ -n "$TEST_DATA" ] && echo "  Test data: $TEST_DATA" || echo "  Mode: smoke test (set TEST_DATA for full suite)"
print_separator
echo ""

for config in "${CONFIGS[@]}"; do
    read -r format clusters nprobe label <<< "$config"

    print_separator
    echo "  [$label] format=$format clusters=$clusters nprobe=$nprobe"
    print_separator

    # Generate the index .bin locally if missing. Docker no longer runs
    # preprocess — the bin is mounted via volume from ./data.
    BIN_PATH="$PROJECT_ROOT/data/$label.bin"
    if [ ! -f "$BIN_PATH" ]; then
        echo "  Index not found at data/$label.bin — generating locally..."
        cd "$PROJECT_ROOT"
        mkdir -p data
        dotnet run --project src/Api/Api.csproj -c Release -- \
            preprocess "$PROJECT_ROOT/resources/references.json.gz" "$BIN_PATH" \
            "$clusters" 20 "$format" "$nprobe"
    else
        echo "  Reusing existing data/$label.bin"
    fi

    if [ "$SKIP_BUILD" = "1" ]; then
        if ! docker image inspect rinha/api:latest > /dev/null 2>&1; then
            echo "  ERROR: image rinha/api:latest not found. Run without SKIP_BUILD first."
            RESULTS+=("$label|NO IMAGE|---|---")
            echo ""
            continue
        fi
        echo "  Reusing image rinha/api:latest"
    else
        echo "  Building Docker image (no preprocess — bin mounted from volume)..."
        cd "$PROJECT_ROOT"
        docker build $PLATFORM_FLAG \
            --build-arg RUNTIME_ID="$RUNTIME_ID" \
            -f docker/Dockerfile -t rinha/api:latest . 2>&1 | tail -5
    fi

    echo "  Starting services with INDEX_PATH=/data/$label.bin..."
    cd "$PROJECT_ROOT"
    INDEX_PATH="/data/$label.bin" $COMPOSE down 2>/dev/null || true
    INDEX_PATH="/data/$label.bin" $COMPOSE up -d

    if ! wait_for_ready 180; then
        RESULTS+=("$label|TIMEOUT|---|---")
        $COMPOSE down 2>/dev/null || true
        echo ""
        continue
    fi

    if [ -n "$TEST_DATA" ] && [ -f "$TEST_DATA" ]; then
        test_output=$(run_full_test "$label" 2>&1)
        echo "$test_output"

        score=$(echo "$test_output" | grep "TOTAL SCORE:" | tail -1 | sed 's/.*TOTAL SCORE:[[:space:]]*//')
        correct=$(echo "$test_output" | grep "Correct:" | sed 's/.*Correct:[[:space:]]*//')
        fp=$(echo "$test_output" | grep "False Positives:" | sed 's/.*False Positives:[[:space:]]*//' | cut -d' ' -f1)
        fn=$(echo "$test_output" | grep "False Negatives:" | sed 's/.*False Negatives:[[:space:]]*//' | cut -d' ' -f1)
        p99=$(echo "$test_output" | grep "^p99:" | sed 's/.*p99:[[:space:]]*//' | cut -d' ' -f1)
        throughput=$(echo "$test_output" | grep "Throughput:" | sed 's/.*Throughput:[[:space:]]*//')
        RESULTS+=("$label|${score:-?}|${correct:-?}|${fp:-?}|${fn:-?}|${p99:-?}|${throughput:-?}")
    else
        if smoke_test; then
            RESULTS+=("$label|SMOKE OK|---|---")
        else
            RESULTS+=("$label|SMOKE FAIL|---|---")
        fi
    fi

    echo "  Stopping services..."
    $COMPOSE down
    echo ""
done

# ── Summary ──────────────────────────────────────────────────────────
echo ""
print_separator
echo "  MATRIX RESULTS SUMMARY"
print_separator
printf "  %-18s %8s  %-18s %4s %4s %10s %12s\n" "CONFIG" "SCORE" "CORRECT" "FP" "FN" "P99(ms)" "THROUGHPUT"
printf "  %-18s %8s  %-18s %4s %4s %10s %12s\n" "------" "-----" "-------" "--" "--" "-------" "----------"
for result in "${RESULTS[@]}"; do
    IFS='|' read -r label score correct fp fn p99 throughput <<< "$result"
    printf "  %-18s %8s  %-18s %4s %4s %10s %12s\n" "$label" "$score" "$correct" "$fp" "$fn" "$p99" "$throughput"
done
print_separator
echo ""

# ── Save results to file ────────────────────────────────────────────
RESULTS_DIR="$PROJECT_ROOT/results"
mkdir -p "$RESULTS_DIR"
RESULTS_FILE="$RESULTS_DIR/matrix-$(date +%Y%m%d-%H%M%S).txt"
{
    print_separator
    echo "  MATRIX RESULTS SUMMARY"
    print_separator
    printf "  %-18s %8s  %-18s %4s %4s %10s %12s\n" "CONFIG" "SCORE" "CORRECT" "FP" "FN" "P99(ms)" "THROUGHPUT"
    printf "  %-18s %8s  %-18s %4s %4s %10s %12s\n" "------" "-----" "-------" "--" "--" "-------" "----------"
    for result in "${RESULTS[@]}"; do
        IFS='|' read -r label score correct fp fn p99 throughput <<< "$result"
        printf "  %-18s %8s  %-18s %4s %4s %10s %12s\n" "$label" "$score" "$correct" "$fp" "$fn" "$p99" "$throughput"
    done
    print_separator
} > "$RESULTS_FILE"
echo "  Results saved to: $RESULTS_FILE"
echo ""
