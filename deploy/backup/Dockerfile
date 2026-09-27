# pg_dump → gzip → age (encrypted before leaving the host) → S3 at another provider (ТЗ 40, «Бэкапы»).
FROM postgres:17-alpine
RUN apk add --no-cache age aws-cli curl tini dcron
COPY backup.sh /usr/local/bin/backup.sh
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/backup.sh /usr/local/bin/entrypoint.sh
ENTRYPOINT ["/sbin/tini", "--", "/usr/local/bin/entrypoint.sh"]
