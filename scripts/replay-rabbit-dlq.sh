#!/usr/bin/env bash
set -euo pipefail

ORDER_API_URL="${ORDER_API_URL:-http://localhost:8080}"
COUNT="${1:-50}"

curl -sS -X POST "${ORDER_API_URL}/ops/replay/rabbit-dlq?count=${COUNT}" -H 'Content-Type: application/json'
echo
