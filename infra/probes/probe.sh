#!/usr/bin/env bash
# Probe from a Russian DC (ТЗ 25, «Пробы»; NFR-03). For every target (node × transport):
#   1. start a local Xray client with that single outbound and a SOCKS inbound;
#   2. through it, fetch our "my IP" endpoint — the exit IP must be the node's IP;
#   3. through it, fetch a resource blocked in RU — «подключено, но данные не идут» shows up here;
#   4. push up/down with the latency to the target's Uptime Kuma push monitor.
# The alert rule «2 пробы × 2 раза подряд» lives in Uptime Kuma (retries = 1 per monitor, a group per target).
#
# Usage: probe.sh targets.json     (env: PROBE_NAME, MYIP_URL, CONTROL_URL)
set -uo pipefail

targets_file="${1:?targets.json}"
: "${MYIP_URL:?}" "${CONTROL_URL:?}"
probe_name="${PROBE_NAME:-probe}"
workdir=$(mktemp -d)
trap 'rm -rf "$workdir"; kill $(jobs -p) 2>/dev/null' EXIT

socks_port=10808
count=$(jq 'length' "$targets_file")

for i in $(seq 0 $((count - 1))); do
    target=$(jq -c ".[$i]" "$targets_file")
    name=$(jq -r '.name' <<<"$target")
    node_ip=$(jq -r '.node_ip' <<<"$target")
    push_url=$(jq -r '.kuma_push_url' <<<"$target")
    port=$((socks_port + i))

    jq -n --argjson outbound "$(jq '.outbound + {tag: "probe"}' <<<"$target")" --argjson port "$port" '{
        log: { loglevel: "warning", access: "none" },
        inbounds: [ { listen: "127.0.0.1", port: $port, protocol: "socks", settings: { udp: false } } ],
        outbounds: [ $outbound ]
    }' > "$workdir/$name.json"

    xray run -c "$workdir/$name.json" > "$workdir/$name.log" 2>&1 &
    xray_pid=$!
    sleep 1

    status=down
    message="no connection"
    started=$(date +%s%3N)

    exit_ip=$(curl -fsS -m 15 --socks5-hostname "127.0.0.1:$port" "$MYIP_URL" 2>/dev/null | tr -d '[:space:]')
    if [[ -z "$exit_ip" ]]; then
        message="handshake ok but no data"
    elif [[ "$exit_ip" != "$node_ip" ]]; then
        message="unexpected exit ip $exit_ip"
    elif ! curl -fsS -m 15 -o /dev/null --socks5-hostname "127.0.0.1:$port" "$CONTROL_URL" 2>/dev/null; then
        message="control resource unreachable"
    else
        status=up
        message="ok"
    fi

    latency=$(( $(date +%s%3N) - started ))
    kill "$xray_pid" 2>/dev/null
    wait "$xray_pid" 2>/dev/null

    echo "[$probe_name] $name: $status ($message, ${latency} ms)"
    if [[ -n "$push_url" && "$push_url" != "null" ]]; then
        curl -fsS -m 10 -G "$push_url" \
            --data-urlencode "status=$status" \
            --data-urlencode "msg=$probe_name: $message" \
            --data-urlencode "ping=$latency" > /dev/null || echo "[$probe_name] push to Kuma failed for $name"
    fi
done
