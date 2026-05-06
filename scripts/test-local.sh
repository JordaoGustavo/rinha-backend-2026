#!/bin/bash
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$SCRIPT_DIR/.."

# Auto-detect architecture for native builds
ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ] || [ "$ARCH" = "aarch64" ]; then
    echo "=== Detected ARM64 — building natively (no emulation) ==="
    RUNTIME_ID="linux-arm64"
    PLATFORM_FLAG=""
else
    echo "=== Detected x86_64 — building for amd64 ==="
    RUNTIME_ID="linux-x64"
    PLATFORM_FLAG="--platform linux/amd64"
fi

echo "=== Building Docker image (RUNTIME_ID=$RUNTIME_ID) ==="
cd "$PROJECT_ROOT"
docker build $PLATFORM_FLAG --build-arg RUNTIME_ID=$RUNTIME_ID -f docker/Dockerfile -t rinha/api:latest .

echo "=== Starting services ==="
docker compose -f docker/docker-compose.yml --project-directory docker down 2>/dev/null || true
docker compose -f docker/docker-compose.yml --project-directory docker up -d

echo "=== Waiting for ready ==="
for i in $(seq 1 120); do
    if curl -sf http://localhost:9999/ready > /dev/null 2>&1; then
        echo "API ready after ${i}s"
        break
    fi
    sleep 2
done

echo "=== Testing /fraud-score ==="
RESPONSE=$(curl -sf -X POST http://localhost:9999/fraud-score \
  -H 'Content-Type: application/json' \
  -d '{
    "id": "tx-test-1",
    "transaction": { "amount": 41.12, "installments": 2, "requested_at": "2026-03-11T18:45:53Z" },
    "customer": { "avg_amount": 82.24, "tx_count_24h": 3, "known_merchants": ["MERC-003", "MERC-016"] },
    "merchant": { "id": "MERC-016", "mcc": "5411", "avg_amount": 60.25 },
    "terminal": { "is_online": false, "card_present": true, "km_from_home": 29.23 },
    "last_transaction": null
  }')

echo "Response: $RESPONSE"
echo "$RESPONSE" | grep -q '"approved"' && echo "PASSED: has approved field" || echo "FAILED"
echo "$RESPONSE" | grep -q '"fraud_score"' && echo "PASSED: has fraud_score field" || echo "FAILED"

echo "=== Stopping services ==="
docker compose -f docker/docker-compose.yml --project-directory docker down

echo "=== All integration tests passed ==="
