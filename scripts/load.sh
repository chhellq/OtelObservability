#!/usr/bin/env bash
# Generate continuous order traffic so the dashboards have something to show.
# Usage:  ./scripts/load.sh [requests_per_second] [duration_seconds]
# Example: ./scripts/load.sh 5 300    # 5 req/s for 5 minutes

set -euo pipefail

RPS=${1:-3}
DURATION=${2:-0}   # 0 = forever
API=${API_URL:-http://localhost:8080}

CUSTOMERS=("alice" "bob" "carol" "dave" "eve" "frank" "grace" "henry")

echo "Generating ~${RPS} req/s against ${API}  (Ctrl+C to stop)"
echo

start=$(date +%s)
i=0
while true; do
    now=$(date +%s)
    if [ "${DURATION}" != "0" ] && [ "$((now - start))" -ge "${DURATION}" ]; then
        break
    fi

    customer=${CUSTOMERS[$((RANDOM % ${#CUSTOMERS[@]}))]}
    # Mix of amounts - occasionally one that gets rejected (>10000)
    amount=$((RANDOM % 100 + 10))
    if [ $((RANDOM % 20)) -eq 0 ]; then
        amount=$((RANDOM % 5000 + 10001))
    fi

    code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${API}/orders" \
        -H 'Content-Type: application/json' \
        -d "{\"customer\":\"${customer}\",\"amount\":${amount}}" || echo "000")

    i=$((i + 1))
    printf "\r[%4d]  customer=%-6s amount=%-6d  status=%s   " "$i" "$customer" "$amount" "$code"

    sleep "$(awk "BEGIN { print 1/${RPS} }")"
done
echo
echo "Done."
