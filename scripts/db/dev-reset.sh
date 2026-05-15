#!/usr/bin/env bash
set -euo pipefail

MIN_SERVER_VERSION_NUM=160000
CONFIRMATION_REQUIRED="I_UNDERSTAND_THIS_DELETES_DATA"
REMOTE_CONFIRMATION_REQUIRED="YES_I_UNDERSTAND"

DB_NAME="${HASHSTORM_DB_NAME:-hashstorm}"
DB_USER="${HASHSTORM_DB_USER:-hashstorm}"
PGPORT="${PGPORT:-5432}"
RESET_CONFIRMATION="${HASHSTORM_DEV_RESET:-}"
ALLOW_REMOTE="${HASHSTORM_DEV_RESET_ALLOW_REMOTE:-}"
RESET_MODE="schema"
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

refuse_dangerous_database_name() {
  local value="${1,,}"

  case "$value" in
    postgres|template0|template1|production|prod|main|default)
      echo "Refusing to reset dangerous database name: $1" >&2
      exit 1
      ;;
  esac
}

is_local_pg_host() {
  local host="${PGHOST:-}"

  [[ -z "$host" ]] ||
    [[ "$host" == "localhost" ]] ||
    [[ "$host" == "127.0.0.1" ]] ||
    [[ "$host" == "::1" ]] ||
    [[ "$host" == /* ]]
}

is_local_pg_hostaddr() {
  local hostaddr="${PGHOSTADDR:-}"

  [[ -z "$hostaddr" ]] ||
    [[ "$hostaddr" == "127.0.0.1" ]] ||
    [[ "$hostaddr" == "::1" ]]
}

requires_remote_override() {
  ! is_local_pg_host ||
    ! is_local_pg_hostaddr ||
    [[ -n "${PGSERVICE:-}" ]]
}

print_remote_guard_reasons() {
  if ! is_local_pg_host; then
    echo "  PGHOST=${PGHOST:-}" >&2
  fi

  if ! is_local_pg_hostaddr; then
    echo "  PGHOSTADDR=${PGHOSTADDR:-}" >&2
  fi

  if [[ -n "${PGSERVICE:-}" ]]; then
    echo "  PGSERVICE=${PGSERVICE}" >&2
  fi
}

psql_app() {
  psql -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" "$@"
}

validate_identifier "HASHSTORM_DB_NAME" "$DB_NAME"
validate_identifier "HASHSTORM_DB_USER" "$DB_USER"
validate_port "$PGPORT"
refuse_dangerous_database_name "$DB_NAME"

if [[ "$RESET_CONFIRMATION" != "$CONFIRMATION_REQUIRED" ]]; then
  echo "Refusing to reset without explicit confirmation." >&2
  echo "Set HASHSTORM_DEV_RESET=$CONFIRMATION_REQUIRED to delete all objects in schema public for database '$DB_NAME'." >&2
  exit 1
fi

if requires_remote_override && [[ "$ALLOW_REMOTE" != "$REMOTE_CONFIRMATION_REQUIRED" ]]; then
  echo "Refusing to reset remote-looking or indirect PostgreSQL target." >&2
  print_remote_guard_reasons
  echo "Set HASHSTORM_DEV_RESET_ALLOW_REMOTE=$REMOTE_CONFIRMATION_REQUIRED only for an intentional development reset." >&2
  exit 1
fi

server_version_num="$(psql_app -Atc "SELECT current_setting('server_version_num')::int")"
if (( server_version_num < MIN_SERVER_VERSION_NUM )); then
  echo "PostgreSQL 16 or newer is required. Server reported server_version_num=$server_version_num." >&2
  exit 1
fi

echo "HashStormCore development database reset"
echo "This is destructive and intended only for local development databases."
echo
echo "Target:"
echo "  PGHOST: ${PGHOST:-local socket/default}"
echo "  PGHOSTADDR: ${PGHOSTADDR:-unset}"
echo "  PGSERVICE: ${PGSERVICE:-unset}"
echo "  port: $PGPORT"
echo "  database: $DB_NAME"
echo "  application user: $DB_USER"
echo "  reset mode: $RESET_MODE"
echo "  PostgreSQL server_version_num: $server_version_num"
echo
echo "This will destroy all objects in schema public with:"
echo "  DROP SCHEMA IF EXISTS public CASCADE;"
echo "It will not reset Redis, filesystem outbox/WAL data, Docker resources, or any other database."
echo

psql_app -v db_user="$DB_USER" <<'SQL'
SELECT format('DROP SCHEMA IF EXISTS %I CASCADE', 'public')
\gexec

SELECT format('CREATE SCHEMA %I AUTHORIZATION %I', 'public', :'db_user')
\gexec

SELECT format('GRANT ALL ON SCHEMA %I TO %I', 'public', :'db_user')
\gexec

GRANT USAGE ON SCHEMA public TO public;
SQL

echo
echo "Development schema reset complete."
echo "Schema was not reapplied automatically."
echo
echo "Recommended next steps:"
echo "  HASHSTORM_DB_NAME=$DB_NAME HASHSTORM_DB_USER=$DB_USER PGPORT=$PGPORT bash scripts/db/apply-schema.sh"
echo "  HASHSTORM_DB_NAME=$DB_NAME HASHSTORM_DB_USER=$DB_USER PGPORT=$PGPORT bash scripts/db/apply-migrations.sh"
echo "  HASHSTORM_DB_NAME=$DB_NAME HASHSTORM_DB_USER=$DB_USER PGPORT=$PGPORT bash scripts/db/apply-indexes.sh"
echo "  HASHSTORM_DB_NAME=$DB_NAME HASHSTORM_DB_USER=$DB_USER PGPORT=$PGPORT bash scripts/db/add-share-partition.sh <poolId>"
echo "  HASHSTORM_DB_NAME=$DB_NAME HASHSTORM_DB_USER=$DB_USER PGPORT=$PGPORT bash scripts/db/check-missing-share-partitions.sh <config.json>"
