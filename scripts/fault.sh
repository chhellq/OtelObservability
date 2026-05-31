#!/usr/bin/env bash
# Inject faults into the external service to demonstrate observability finding them.
# Usage:
#   ./scripts/fault.sh status               - show current fault state
#   ./scripts/fault.sh latency <ms>         - inject latency on every call
#   ./scripts/fault.sh errors <0..1>        - inject errors with this probability
#   ./scripts/fault.sh clear                - turn all faults off

set -euo pipefail

EXT=${EXT_URL:-http://localhost:8090}
cmd=${1:-status}

case "$cmd" in
    status)
        curl -s "${EXT}/admin/faults" | jq .
        ;;
    latency)
        ms=${2:?usage: fault.sh latency <ms>}
        curl -s -X POST "${EXT}/admin/faults" -H 'Content-Type: application/json' \
            -d "{\"latencyMs\":${ms}}" | jq .
        ;;
    errors)
        rate=${2:?usage: fault.sh errors <0..1>}
        curl -s -X POST "${EXT}/admin/faults" -H 'Content-Type: application/json' \
            -d "{\"errorRate\":${rate}}" | jq .
        ;;
    clear)
        curl -s -X POST "${EXT}/admin/faults" -H 'Content-Type: application/json' \
            -d '{"latencyMs":0,"errorRate":0}' | jq .
        ;;
    *)
        echo "Unknown command: $cmd"
        echo "Usage: $0 {status|latency <ms>|errors <0..1>|clear}"
        exit 1
        ;;
esac
