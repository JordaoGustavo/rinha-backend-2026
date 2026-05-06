#!/bin/bash
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
RESOURCES="$SCRIPT_DIR/../resources"
mkdir -p "$RESOURCES"

BASE_URL="https://raw.githubusercontent.com/zanfranceschi/rinha-de-backend-2026/main/resources"

if [ ! -f "$RESOURCES/references.json.gz" ]; then
    echo "Downloading references.json.gz..."
    curl -L -o "$RESOURCES/references.json.gz" "$BASE_URL/references.json.gz"
fi

if [ ! -f "$RESOURCES/mcc_risk.json" ]; then
    echo "Downloading mcc_risk.json..."
    curl -L -o "$RESOURCES/mcc_risk.json" "$BASE_URL/mcc_risk.json"
fi

if [ ! -f "$RESOURCES/normalization.json" ]; then
    echo "Downloading normalization.json..."
    curl -L -o "$RESOURCES/normalization.json" "$BASE_URL/normalization.json"
fi

echo "Resources ready in $RESOURCES"
