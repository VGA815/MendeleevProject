#!/usr/bin/env bash
# Builds the value of the `routing` subscription header for Happ (ТЗ 24, «Заголовки подписки»; ТЗ 25,
# «Раздельная маршрутизация»): base64 of the profile JSON, prefixed with happ://routing/add/.
# Paste the result into the Remnawave subscription settings (response rules for Happ).
#
# Happ refreshes geo files at most weekly, so urgent exceptions go into the profile as explicit domains,
# not through geosite. The order of ProxySites/DirectSites is checked on the prototype (ТЗ 99).
set -euo pipefail

profile="${1:-$(dirname "$0")/happ-routing.json}"
jq -e . "$profile" > /dev/null
echo "happ://routing/add/$(jq -c . "$profile" | base64 -w0)"
