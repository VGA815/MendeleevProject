#!/bin/sh
# Monthly restore drill (ТЗ 40: «Проверка восстановления раз в месяц»): takes the latest dump, restores it
# into a throwaway PostgreSQL container and runs control queries. Run on the tech admin's machine:
#   AGE_IDENTITY=~/.config/mendeleev/backup.key S3_ENDPOINT=… S3_BUCKET=… ./restore-check.sh
# The panel's dump: BACKUP_PREFIX=panel ./restore-check.sh
set -eu

: "${AGE_IDENTITY:?path to the age private key}"
: "${S3_ENDPOINT:?}"
: "${S3_BUCKET:?}"

prefix="${BACKUP_PREFIX:-db}"
latest=$(aws --endpoint-url "${S3_ENDPOINT}" s3 ls "s3://${S3_BUCKET}/${prefix}/" | sort | tail -n 1 | awk '{print $4}')
[ -n "${latest}" ] || { echo "no dumps under ${prefix}/" >&2; exit 1; }
echo "latest dump: ${prefix}/${latest}"
aws --endpoint-url "${S3_ENDPOINT}" s3 cp "s3://${S3_BUCKET}/${prefix}/${latest}" "/tmp/${latest}" --only-show-errors

container=mendeleev-restore-check
docker rm -f "${container}" > /dev/null 2>&1 || true
docker run -d --name "${container}" -e POSTGRES_PASSWORD=check -e POSTGRES_DB=restore postgres:17-alpine > /dev/null
trap 'docker rm -f "${container}" > /dev/null' EXIT
# Over TCP: during initdb the image runs a temporary server on the socket only, then restarts it.
until docker exec "${container}" pg_isready -h 127.0.0.1 -U postgres > /dev/null 2>&1; do sleep 1; done

age -d -i "${AGE_IDENTITY}" "/tmp/${latest}" | gunzip | docker exec -i "${container}" psql -q -U postgres -d restore > /dev/null
rm -f "/tmp/${latest}"

if [ "${prefix}" = "panel" ]; then
    # Remnawave's own tables (prisma/schema.prisma of the pinned version).
    docker exec "${container}" psql -v ON_ERROR_STOP=1 -U postgres -d restore -c "
      SELECT (SELECT count(*) FROM users) AS users,
             (SELECT count(*) FROM nodes) AS nodes,
             (SELECT count(*) FROM hosts) AS hosts,
             (SELECT count(*) FROM internal_squads) AS squads;"
else
    docker exec "${container}" psql -v ON_ERROR_STOP=1 -U postgres -d restore -c "
      SELECT (SELECT count(*) FROM users) AS users,
             (SELECT count(*) FROM subscriptions WHERE status IN ('trial', 'active')) AS active_subscriptions,
             (SELECT max(paid_at) FROM payments) AS last_payment,
             (SELECT max(created_at) FROM audit_log) AS last_audit;"
fi
echo "restore check passed"
