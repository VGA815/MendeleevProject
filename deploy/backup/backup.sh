#!/bin/sh
# Daily dump of a database (RPO ≤ 24 h, NFR-09): the service's on the management VPS, the panel's on the panel
# VPS (ТЗ 40, «Бэкапы и восстановление»). The dump is encrypted with age before upload, so the storage provider
# never sees plain data. Success is pushed to Uptime Kuma; no push for a day = alert.
# BACKUP_PREFIX separates the two in the bucket: db/ (the service, default) and panel/.
set -eu

: "${BACKUP_AGE_RECIPIENT:?age public key is required}"
: "${S3_BUCKET:?}"
: "${S3_ENDPOINT:?}"

prefix="${BACKUP_PREFIX:-db}"
stamp=$(date -u +%Y%m%dT%H%M%SZ)
file="/tmp/${PGDATABASE}-${stamp}.sql.gz.age"

echo "[backup] dumping ${PGDATABASE}"
pg_dump --no-owner --no-privileges --format=plain "${PGDATABASE}" \
    | gzip -9 \
    | age -r "${BACKUP_AGE_RECIPIENT}" -o "${file}"

echo "[backup] uploading $(du -h "${file}" | cut -f1)"
aws --endpoint-url "${S3_ENDPOINT}" --region "${S3_REGION:-ru-1}" \
    s3 cp "${file}" "s3://${S3_BUCKET}/${prefix}/$(basename "${file}")" --only-show-errors
rm -f "${file}"

# Retention: 30 days (ТЗ 40).
cutoff=$(date -u -d "@$(( $(date +%s) - ${BACKUP_RETENTION_DAYS:-30} * 86400 ))" +%Y%m%d%H%M%S 2>/dev/null || echo "")
if [ -n "${cutoff}" ]; then
    aws --endpoint-url "${S3_ENDPOINT}" --region "${S3_REGION:-ru-1}" s3 ls "s3://${S3_BUCKET}/${prefix}/" \
        | awk '{print $4}' \
        | while read -r name; do
            ts=$(echo "${name}" | sed -n 's/.*-\([0-9]\{8\}T[0-9]\{6\}Z\)\.sql\.gz\.age$/\1/p')
            if [ -n "${ts}" ] && [ "$(echo "${ts}" | tr -d TZ)" -lt "${cutoff}" ]; then
                aws --endpoint-url "${S3_ENDPOINT}" --region "${S3_REGION:-ru-1}" s3 rm "s3://${S3_BUCKET}/${prefix}/${name}" --only-show-errors
            fi
        done
fi

if [ -n "${KUMA_PUSH_URL:-}" ]; then
    curl -fsS -m 10 "${KUMA_PUSH_URL}?status=up&msg=${prefix}-${stamp}" > /dev/null || echo "[backup] Kuma push failed"
fi

echo "[backup] done ${prefix}/${stamp}"
