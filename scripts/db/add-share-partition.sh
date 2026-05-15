#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SQL_FILE="$ROOT/scripts/db/add-share-partition.sql"
MIN_SERVER_VERSION_NUM=160000

DB_NAME="${HASHSTORM_DB_NAME:-hashstorm}"
DB_USER="${HASHSTORM_DB_USER:-hashstorm}"
PGPORT="${PGPORT:-5432}"
POOL_ID="${1:-}"
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

validate_pool_id() {
  local value="$1"

  if [[ -z "$value" ]]; then
    echo "Usage: bash scripts/db/add-share-partition.sh <poolId>" >&2
    exit 1
  fi

  if [[ ! "$value" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$ ]]; then
    echo "poolId must match ^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$" >&2
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
validate_pool_id "$POOL_ID"
require_file "$SQL_FILE"

server_version_num="$(psql_app -Atc "SELECT current_setting('server_version_num')::int")"
if (( server_version_num < MIN_SERVER_VERSION_NUM )); then
  echo "PostgreSQL 16 or newer is required. Server reported server_version_num=$server_version_num." >&2
  exit 1
fi

echo "Adding shares partition for pool '$POOL_ID' in database '$DB_NAME' as user '$DB_USER'."
echo "This command requires public.shares to already be partitioned by LIST (poolid)."
echo "It does not convert existing non-partitioned shares tables and does not create a default partition."
echo "PostgreSQL server_version_num=$server_version_num."

psql_app \
  -v pool_id="$POOL_ID" \
  -f "$SQL_FILE"

echo
echo "Share partition check complete for pool '$POOL_ID'."
