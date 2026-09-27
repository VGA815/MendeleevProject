#!/usr/bin/env bash
# Points a DNS record to a new target through the Cloudflare API (DNS only, no proxying; TTL 300 —
# ТЗ 40, «Домены и TLS»). Used by the domain-switch and management-VPS-restore runbooks.
#
#   cf-dns.sh <zone-name> <record-name> <A|CNAME> <content>
#
# env: CF_API_TOKEN (zone DNS edit rights)
set -euo pipefail

zone="${1:?zone, e.g. example-subs.com}"
name="${2:?record, e.g. sub.example-subs.com}"
type="${3:?A or CNAME}"
content="${4:?IP or hostname}"
: "${CF_API_TOKEN:?}"

cf() {
    curl -fsS -H "Authorization: Bearer $CF_API_TOKEN" -H "Content-Type: application/json" "$@"
}

zone_id=$(cf "https://api.cloudflare.com/client/v4/zones?name=$zone" | jq -r '.result[0].id')
[[ "$zone_id" != "null" ]] || { echo "zone $zone not found" >&2; exit 1; }

record_id=$(cf "https://api.cloudflare.com/client/v4/zones/$zone_id/dns_records?name=$name&type=$type" | jq -r '.result[0].id')
payload=$(jq -n --arg type "$type" --arg name "$name" --arg content "$content" \
    '{type: $type, name: $name, content: $content, ttl: 300, proxied: false}')

if [[ "$record_id" == "null" ]]; then
    cf -X POST "https://api.cloudflare.com/client/v4/zones/$zone_id/dns_records" -d "$payload" | jq -r '.success'
else
    cf -X PUT "https://api.cloudflare.com/client/v4/zones/$zone_id/dns_records/$record_id" -d "$payload" | jq -r '.success'
fi
