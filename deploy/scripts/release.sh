#!/bin/sh
# Release on the management VPS (ТЗ 40, «Сборка и поставка»):
#   pull → migrations (bundle) → up → wait for /health/ready → roll back to the previous tag if not ready.
# Not during peak hours and not during launch waves. Usage: ./scripts/release.sh <new-tag>
set -eu
cd "$(dirname "$0")/.."

new_tag="${1:?usage: release.sh <image tag>}"
previous_tag=$(grep '^APP_TAG=' .env | cut -d= -f2)

set_tag() {
    sed -i "s/^APP_TAG=.*/APP_TAG=$1/" .env
}

echo "release ${previous_tag} -> ${new_tag}"
set_tag "${new_tag}"

docker compose pull app migrator
docker compose run --rm migrator
docker compose up -d app

for _ in $(seq 1 30); do
    status=$(docker inspect --format '{{.State.Health.Status}}' "$(docker compose ps -q app)" 2>/dev/null || echo starting)
    if [ "${status}" = "healthy" ]; then
        echo "app is healthy"
        exit 0
    fi
    sleep 5
done

echo "app did not become ready, rolling back to ${previous_tag}" >&2
# Migrations are additive by convention, so the previous version runs on the new schema.
set_tag "${previous_tag}"
docker compose up -d app
exit 1
