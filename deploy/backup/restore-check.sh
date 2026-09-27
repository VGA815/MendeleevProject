#!/bin/sh
# Monthly restore drill (ТЗ 40: «Проверка восстановления раз в месяц»): takes the latest dump, restores it
# into a throwaway PostgreSQL container and runs control queries. Run on the tech admin's machine:
#   AGE_IDENTITY=~/.config/mendeleev/backup.key S3_ENDPOINT=… S3_BUCKET=… ./restore-check.sh
set -eu

: "${AGE_IDENTITY:?path to the age private key}"
: "${S3_ENDPOINT:?}"
: "${S3_BUCKET:?}"

latest=$(aws --endpoint-url "${S3_ENDPOINT}" s3 ls "s3://${S3_BUCKET}/db/" | sort | tail -n 1 | awk '{print $4}')
echo "latest dump: ${latest}"
aws --endpoint-url "${S3_ENDPOINT}" s3 cp "s3://${S3_BUCKET}/db/${latest}" "/tmp/${latest}" --only-show-errors

container=mendeleev-restore-check
docker rm -f "${container}" > /dev/null 2>&1 || true
docker run -d --name "${container}" -e POSTGRES_PASSWORD=check -e POSTGRES_DB=restore postgres:17-alpine > /dev/null
trap 'docker rm -f "${container}" > /dev/null' EXIT
until docker exec "${container}" pg_isready -U postgres > /dev/null 2>&1; do sleep 1; done

age -d -i "${AGE_IDENTITY}" "/tmp/${latest}" | gunzip | docker exec -i "${container}" psql -q -U postgres -d restore > /dev/null
rm -f "/tmp/${latest}"

docker exec "${container}" psql -U postgres -d restore -c "
  SELECT (SELECT count(*) FROM users) AS users,
         (SELECT count(*) FROM subscriptions WHERE status IN ('trial', 'active')) AS active_subscriptions,
         (SELECT max(paid_at) FROM payments) AS last_payment,
         (SELECT max(created_at) FROM audit_log) AS last_audit;"
echo "restore check passed"
