#!/bin/sh
# Runs once, when the data directory is created. Two roles (ТЗ 12, «Индексы и ограничения»):
#   POSTGRES_USER      — owner: migrations and backups;
#   POSTGRES_APP_USER  — the service at runtime: DML only, no UPDATE on audit_log.
# The audit_log trigger (migration Initial) additionally forbids UPDATE and deleting rows younger than a year.
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
	CREATE ROLE ${POSTGRES_APP_USER} LOGIN PASSWORD '${POSTGRES_APP_PASSWORD}';
	GRANT CONNECT ON DATABASE ${POSTGRES_DB} TO ${POSTGRES_APP_USER};
	GRANT USAGE ON SCHEMA public TO ${POSTGRES_APP_USER};

	-- Tables and sequences created later by the owner's migrations.
	ALTER DEFAULT PRIVILEGES FOR ROLE ${POSTGRES_USER} IN SCHEMA public
	    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ${POSTGRES_APP_USER};
	ALTER DEFAULT PRIVILEGES FOR ROLE ${POSTGRES_USER} IN SCHEMA public
	    GRANT USAGE, SELECT ON SEQUENCES TO ${POSTGRES_APP_USER};

	CREATE EXTENSION IF NOT EXISTS citext;
EOSQL
