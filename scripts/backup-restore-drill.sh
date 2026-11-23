#!/usr/bin/env bash
# Backup/restore drill for an isolated PostgreSQL 18 database.
#
# It dumps the source database with pg_dump, restores into a separate restore
# database, and compares row counts plus content checksums for core tables.
# Intended for CI/isolated environments only; never point it at production.
set -euo pipefail

PGHOST="${PGHOST:-127.0.0.1}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-planna}"
PGPASSWORD="${PGPASSWORD:?PGPASSWORD is required}"
PGDATABASE="${PGDATABASE:-planna_test}"
RESTORE_DB="${RESTORE_DB:-planna_restore}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORKDIR="$ROOT/.backup-drill"
DUMP="/work/.backup-drill/planna.dump"

mkdir -p "$WORKDIR"
rm -f "$WORKDIR/planna.dump"

run_pg() {
  docker run --rm --network host \
    -v "$ROOT:/work" -w /work \
    -e PGPASSWORD="$PGPASSWORD" \
    postgres:18 "$@"
}

echo "dumping $PGDATABASE from $PGHOST:$PGPORT"
run_pg pg_dump -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" \
  -Fc --no-owner --no-privileges -f "$DUMP"

echo "recreating $RESTORE_DB"
run_pg psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d postgres -v ON_ERROR_STOP=1 \
  -c "DROP DATABASE IF EXISTS \"$RESTORE_DB\";" \
  -c "CREATE DATABASE \"$RESTORE_DB\";"

echo "restoring into $RESTORE_DB"
run_pg pg_restore -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$RESTORE_DB" \
  --no-owner --no-privileges --exit-on-error "$DUMP"

for table in users tasks task_lists habits habit_logs notes goals workday_overrides attachment_rate_windows; do
  source_count="$(run_pg psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" \
    -Atc "SELECT count(*) FROM $table")"
  restore_count="$(run_pg psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$RESTORE_DB" \
    -Atc "SELECT count(*) FROM $table")"
  if [[ "$source_count" != "$restore_count" ]]; then
    echo "row count mismatch for $table: source=$source_count restore=$restore_count" >&2
    exit 1
  fi
  echo "ok $table rows=$source_count"
done

for table in users tasks task_lists habits habit_logs notes goals workday_overrides; do
  checksum_sql="SELECT md5(coalesce(string_agg(t::text, ',' ORDER BY id), '')) FROM $table t"
  source_sum="$(run_pg psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" \
    -Atc "$checksum_sql")"
  restore_sum="$(run_pg psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$RESTORE_DB" \
    -Atc "$checksum_sql")"
  if [[ "$source_sum" != "$restore_sum" ]]; then
    echo "content checksum mismatch for $table" >&2
    exit 1
  fi
  echo "ok $table checksum=$source_sum"
done

compare_count() {
  local label="$1"
  local sql="$2"
  local source_count restore_count
  source_count="$(run_pg psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" \
    -Atc "$sql")"
  restore_count="$(run_pg psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$RESTORE_DB" \
    -Atc "$sql")"
  if [[ "$source_count" != "$restore_count" ]]; then
    echo "object count mismatch for $label: source=$source_count restore=$restore_count" >&2
    exit 1
  fi
  echo "ok $label count=$source_count"
}

compare_count "columns" \
  "SELECT count(*) FROM information_schema.columns WHERE table_schema='public'"
compare_count "indexes" \
  "SELECT count(*) FROM pg_indexes WHERE schemaname='public'"
compare_count "constraints" \
  "SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname='public'"
compare_count "sequences" \
  "SELECT count(*) FROM information_schema.sequences WHERE sequence_schema='public'"

echo "backup/restore drill passed (row counts + content checksums + object counts)"
