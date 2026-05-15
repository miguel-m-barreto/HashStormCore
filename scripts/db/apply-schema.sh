#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CREATEDB_SQL="$ROOT/src/HashStormCore/Persistence/Postgres/Scripts/createdb.sql"
EVENT_PIPELINE_SQL="$ROOT/src/HashStormCore/Persistence/Postgres/Scripts/event_pipeline.sql"
MIN_SERVER_VERSION_NUM=160000

DB_NAME="${HASHSTORM_DB_NAME:-hashstorm}"
DB_USER="${HASHSTORM_DB_USER:-hashstorm}"
ALLOW_EXISTING="${HASHSTORM_APPLY_SCHEMA_ALLOW_EXISTING:-0}"
PGPORT="${PGPORT:-5432}"
export PGPORT

validate_identifier() {
  local label="$1"
  local value="$2"

  if [[ -z "$value" ]]; then
    echo "$label must not be empty" >&2
    exit 1
  fi

  if [[ ! "$value" =~ ^[A-Za-z_][A-Za-z0-9_]{0,62}$ ]]; then
    echo "$label must match ^[A-Za-z_][A-Za-z0-9_]{0,62}$" >&2
    exit 1
  fi
}

validate_port() {
  local value="$1"

  if [[ ! "$value" =~ ^[0-9]+$ ]] || (( value < 1 || value > 65535 )); then
    echo "PGPORT must be a number between 1 and 65535" >&2
    exit 1
  fi
}

require_file() {
  local file="$1"

  if [[ ! -f "$file" ]]; then
    echo "Required file not found: $file" >&2
    exit 1
  fi
}

psql_app() {
  psql -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" "$@"
}

validate_identifier "HASHSTORM_DB_NAME" "$DB_NAME"
validate_identifier "HASHSTORM_DB_USER" "$DB_USER"
validate_port "$PGPORT"
require_file "$CREATEDB_SQL"
require_file "$EVENT_PIPELINE_SQL"

server_version_num="$(psql_app -Atc "SELECT current_setting('server_version_num')::int")"
if (( server_version_num < MIN_SERVER_VERSION_NUM )); then
  echo "PostgreSQL 16 or newer is required. Server reported server_version_num=$server_version_num." >&2
  exit 1
fi

existing_tables="$(psql_app -At <<'SQL'
SELECT COALESCE(string_agg(c.relname, ', ' ORDER BY c.relname), '')
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public'
  AND c.relkind IN ('r', 'p')
  AND c.relname IN (
    'shares',
    'blocks',
    'balances',
    'balance_changes',
    'miner_settings',
    'payments',
    'poolstats',
    'minerstats',
    'share_events',
    'event_pipeline_processed_events'
  );
SQL
)"

if [[ -n "$existing_tables" && "$ALLOW_EXISTING" != "1" ]]; then
  echo "Refusing to apply schema because existing HashStormCore tables were found: $existing_tables" >&2
  echo "createdb.sql is DB-empty-only. Set HASHSTORM_APPLY_SCHEMA_ALLOW_EXISTING=1 only if you intentionally want psql to attempt the scripts anyway." >&2
  exit 1
fi

echo "Applying HashStormCore schema to database '$DB_NAME' as user '$DB_USER'."
echo "PostgreSQL server_version_num=$server_version_num."
echo "createdb.sql is DB-empty-only; event_pipeline.sql is applied after it."
echo "Legacy destructive scripts are not run by this command."

psql_app -f "$CREATEDB_SQL"
psql_app -f "$EVENT_PIPELINE_SQL"

echo
echo "Schema application complete."
echo "Read-only status check:"
echo "  HASHSTORM_DB_NAME=$DB_NAME HASHSTORM_DB_USER=$DB_USER PGPORT=$PGPORT psql -v ON_ERROR_STOP=1 -U $DB_USER -d $DB_NAME -f scripts/db/status.sql"
