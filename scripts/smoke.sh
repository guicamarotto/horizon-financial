#!/usr/bin/env bash
set -euo pipefail

ORDER_API_URL="${ORDER_API_URL:-http://localhost:8080}"
ACCOUNT_ID="${ACCOUNT_ID:-$(cat /proc/sys/kernel/random/uuid)}"
SYMBOL="${SYMBOL:-PETR4}"
SIDE="${SIDE:-BUY}"
QUANTITY="${QUANTITY:-100}"
PRICE="${PRICE:-32.15}"

payload=$(cat <<JSON
{
  "accountId": "${ACCOUNT_ID}",
  "symbol": "${SYMBOL}",
  "side": "${SIDE}",
  "quantity": ${QUANTITY},
  "price": ${PRICE}
}
JSON
)

echo "[smoke] Creating order at ${ORDER_API_URL}/orders"
response=$(curl -sS -X POST "${ORDER_API_URL}/orders" \
  -H 'Content-Type: application/json' \
  -H "x-correlation-id: $(cat /proc/sys/kernel/random/uuid)" \
  -d "${payload}")

echo "[smoke] Create response: ${response}"
order_id=$(python3 - <<'PY' "$response"
import json,sys
obj=json.loads(sys.argv[1])
print(obj["orderId"])
PY
)

if [[ -z "${order_id}" ]]; then
  echo "[smoke] Could not parse order id"
  exit 1
fi

echo "[smoke] Polling status for order ${order_id}"
final_status=""
for _ in $(seq 1 60); do
  http_payload=$(curl -sS -w '\n%{http_code}' "${ORDER_API_URL}/orders/${order_id}")
  http_code=$(echo "${http_payload}" | tail -n1)
  order_response=$(echo "${http_payload}" | sed '$d')

  if [[ "${http_code}" != "200" ]]; then
    echo "[smoke] status endpoint not ready yet (http=${http_code})"
    sleep 2
    continue
  fi

  status=$(python3 - <<'PY' "$order_response"
import json,sys
obj=json.loads(sys.argv[1])
print(obj.get("status", "UNKNOWN"))
PY
)
  echo "[smoke] status=${status}"

  if [[ "${status}" == "LIMIT_RESERVED" || "${status}" == "REJECTED" ]]; then
    final_status="${status}"
    break
  fi

  sleep 2
done

if [[ -z "${final_status}" ]]; then
  echo "[smoke] Timeout waiting for final status"
  exit 1
fi

echo "[smoke] Final status: ${final_status}"
echo "[smoke] Validating notification row in Postgres"
notif_count=$(docker exec postgres psql -U postgres -d finance_lab -t -A -c "SELECT COUNT(*) FROM notifications.notifications WHERE order_id = '${order_id}';" | tr -d '[:space:]')

echo "[smoke] Notifications for order ${order_id}: ${notif_count}"
if [[ "${notif_count}" == "0" ]]; then
  echo "[smoke] Expected at least one notification"
  exit 1
fi

echo "[smoke] Success"
