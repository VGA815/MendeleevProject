#!/usr/bin/env bash
# Runbook «Нода недоступна из РФ» (ТЗ 25, 40; NFR-04: замена ≤ 30 мин). In the MVP it runs on command;
# automatic replacement on an alert comes in stage 1.5 (FR-NODE-18).
#
#   replace-node.sh <blocked-tag> <reserve-tag>
#
# Hosts in Remnawave carry the node code among their tags (DE1, NL1, RES1 …; `tags` is an array in 3.x).
# The blocked node's hosts are disabled (they leave the subscriptions), the reserve's hosts are enabled.
# Clients pick the change up at the next subscription update (profile-update-interval = 2 h); on a mass
# outage send the «Инцидент» broadcast from the bot so that users update right away. Then refill the
# reserve: ansible-playbook playbooks/node.yml.
#
# env: REMNAWAVE_URL (through Caddy on the panel VPS), REMNAWAVE_TOKEN (an API token with scope hosts:*)
set -euo pipefail

blocked="${1:?blocked host tag, e.g. DE1}"
reserve="${2:?reserve host tag, e.g. RES1}"
: "${REMNAWAVE_URL:?}" "${REMNAWAVE_TOKEN:?}"

api() {
    local method=$1 path=$2 body=${3:-}
    curl -fsS -X "$method" "${REMNAWAVE_URL%/}/api/$path" \
        -H "Authorization: Bearer $REMNAWAVE_TOKEN" \
        -H "Content-Type: application/json" \
        ${body:+-d "$body"}
}

hosts=$(api GET hosts | jq '.response')
blocked_uuids=$(jq -c --arg tag "$blocked" '[.[] | select((.tags // []) | any(. == $tag)) | .uuid]' <<<"$hosts")
reserve_uuids=$(jq -c --arg tag "$reserve" '[.[] | select((.tags // []) | any(. == $tag)) | .uuid]' <<<"$hosts")

if [[ "$(jq length <<<"$reserve_uuids")" == 0 ]]; then
    echo "No hosts tagged $reserve — nothing to switch to." >&2
    exit 1
fi

echo "Enabling $(jq length <<<"$reserve_uuids") host(s) of $reserve"
api POST hosts/bulk/enable "{\"uuids\": $reserve_uuids}" > /dev/null

echo "Disabling $(jq length <<<"$blocked_uuids") host(s) of $blocked"
api POST hosts/bulk/disable "{\"uuids\": $blocked_uuids}" > /dev/null

echo "Done. Next: check the probes for $reserve, send the incident broadcast if many users are affected,"
echo "and provision a new reserve node (ansible-playbook playbooks/node.yml --limit <new-node>)."
