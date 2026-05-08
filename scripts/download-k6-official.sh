#!/bin/bash
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DEST="$SCRIPT_DIR/k6-official/test"
mkdir -p "$DEST"

BASE_URL="https://raw.githubusercontent.com/zanfranceschi/rinha-de-backend-2026/main/test"

for f in test.js smoke.js test-data.json; do
    if [ ! -f "$DEST/$f" ]; then
        echo "Downloading $f..."
        curl -fL -o "$DEST/$f" "$BASE_URL/$f"
    fi
done

echo "Official k6 ready in $DEST"
