#!/bin/sh
# Runs backup.sh on BACKUP_CRON. `docker compose run --rm backup backup.sh` makes a dump right now.
set -eu

if [ "$#" -gt 0 ]; then
    exec "$@"
fi

env | grep -E '^(PG|S3_|AWS_|BACKUP_|KUMA_)' | sed 's/^/export /; s/=/="/; s/$/"/' > /etc/backup.env
echo "${BACKUP_CRON} . /etc/backup.env && /usr/local/bin/backup.sh >> /proc/1/fd/1 2>&1" > /etc/crontabs/root
exec crond -f -l 8
