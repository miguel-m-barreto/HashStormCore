#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CREATEDB_SQL="$ROOT/src/HashStormCore/Persistence/Postgres/Scripts/createdb.sql"
EVENT_PIPELINE_SQL="$ROOT/src/HashStormCore/Persistence/Postgres/Scripts/event_pipeline.sql"

prompt_default() {
  local prompt="$1"
  local default="$2"
  local value

  read -r -p "$prompt [$default]: " value
  printf '%s' "${value:-$default}"
}

prompt_secret_default() {
  local prompt="$1"
  local default="$2"
  local value

  read -r -s -p "$prompt [hidden default]: " value
  printf '\n' >&2
  printf '%s' "${value:-$default}"
}

confirm_default_yes() {
  local prompt="$1"
  local value

  read -r -p "$prompt [Y/n]: " value
  [[ -z "$value" || "$value" =~ ^[Yy]$ || "$value" =~ ^[Yy][Ee][Ss]$ ]]
}

confirm_default_no() {
  local prompt="$1"
  local value

  read -r -p "$prompt [y/N]: " value
  [[ "$value" =~ ^[Yy]$ || "$value" =~ ^[Yy][Ee][Ss]$ ]]
}

validate_identifier() {
  local label="$1"
  local value="$2"

  if [[ ! "$value" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
    echo "$label must match ^[A-Za-z_][A-Za-z0-9_]*$" >&2
    exit 1
  fi
}

validate_port() {
  local value="$1"

  if [[ ! "$value" =~ ^[0-9]+$ ]] || (( value < 1 || value > 65535 )); then
    echo "PostgreSQL port must be a number between 1 and 65535" >&2
    exit 1
  fi
}

sql_quote_literal() {
  local escaped="${1//\'/\'\'}"
  printf "'%s'" "$escaped"
}

strip_set_role() {
  local file="$1"

  sed '/^[[:space:]]*SET[[:space:]]\+ROLE[[:space:]]/Id' "$file"
}

require_file() {
  local file="$1"

  if [[ ! -f "$file" ]]; then
    echo "Required file not found: $file" >&2
    exit 1
  fi
}

psql_admin() {
  if [[ -n "${PG_ADMIN_PASSWORD:-}" ]]; then
    PGPASSWORD="$PG_ADMIN_PASSWORD" psql -v ON_ERROR_STOP=1 -h "$PG_HOST" -p "$PG_PORT" -U "$PG_ADMIN_USER" "$@"
  else
    psql -v ON_ERROR_STOP=1 -h "$PG_HOST" -p "$PG_PORT" -U "$PG_ADMIN_USER" "$@"
  fi
}

require_file "$CREATEDB_SQL"
require_file "$EVENT_PIPELINE_SQL"

echo "HashStormCore PostgreSQL setup"
echo

PG_HOST="$(prompt_default "PostgreSQL host" "${PGHOST:-127.0.0.1}")"
PG_PORT="$(prompt_default "PostgreSQL port" "${PGPORT:-5432}")"
PG_ADMIN_USER="$(prompt_default "PostgreSQL admin/superuser" "${PGUSER:-postgres}")"
PG_ADMIN_PASSWORD="$(prompt_secret_default "PostgreSQL admin password; leave empty for .pgpass/no password" "")"

validate_port "$PG_PORT"

DB_NAME="$(prompt_default "Database name" "hashstorm")"
DB_USER="$(prompt_default "Database username/owner" "hashstorm")"
DB_PASSWORD="$(prompt_secret_default "Database user password" "hashstorm")"

validate_identifier "Database name" "$DB_NAME"
validate_identifier "Database username" "$DB_USER"

DB_PASSWORD_SQL="$(sql_quote_literal "$DB_PASSWORD")"

echo
echo "Creating/updating role '$DB_USER' and database '$DB_NAME' on $PG_HOST:$PG_PORT ..."

if [[ "$(psql_admin -d postgres -Atc "SELECT 1 FROM pg_roles WHERE rolname = '$DB_USER'")" == "1" ]]; then
  psql_admin -d postgres -c "ALTER ROLE \"$DB_USER\" WITH LOGIN PASSWORD $DB_PASSWORD_SQL;"
else
  psql_admin -d postgres -c "CREATE ROLE \"$DB_USER\" WITH LOGIN PASSWORD $DB_PASSWORD_SQL;"
fi

if [[ "$(psql_admin -d postgres -Atc "SELECT 1 FROM pg_database WHERE datname = '$DB_NAME'")" != "1" ]]; then
  psql_admin -d postgres -c "CREATE DATABASE \"$DB_NAME\" OWNER \"$DB_USER\";"
else
  echo "Database '$DB_NAME' already exists."

  current_owner="$(psql_admin -d postgres -Atc "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_database WHERE datname = '$DB_NAME'")"
  if [[ "$current_owner" != "$DB_USER" ]]; then
    echo "Database '$DB_NAME' is currently owned by '$current_owner'."
    if confirm_default_no "Change database owner to '$DB_USER'"; then
      psql_admin -d postgres -c "ALTER DATABASE \"$DB_NAME\" OWNER TO \"$DB_USER\";"
    fi
  fi
fi

psql_admin -d postgres -c "GRANT ALL PRIVILEGES ON DATABASE \"$DB_NAME\" TO \"$DB_USER\";"

# PostgreSQL 15+ may restrict CREATE on public. Grant it explicitly for local/dev setup.
psql_admin -d "$DB_NAME" -c "GRANT USAGE, CREATE ON SCHEMA public TO \"$DB_USER\";" >/dev/null

shares_table="$(psql_admin -d "$DB_NAME" -Atc "SELECT to_regclass('public.shares')")"
share_events_table="$(psql_admin -d "$DB_NAME" -Atc "SELECT to_regclass('public.share_events')")"

apply_base_schema="false"
if [[ -z "$shares_table" ]]; then
  if confirm_default_yes "Apply base Pool Core schema from createdb.sql now"; then
    apply_base_schema="true"
  fi
else
  echo "Base table 'shares' already exists; skipping createdb.sql by default."
  if confirm_default_no "Re-apply createdb.sql anyway? This will usually fail on existing tables"; then
    apply_base_schema="true"
  fi
fi

apply_event_schema="false"
if [[ -z "$share_events_table" ]]; then
  if confirm_default_yes "Apply event pipeline schema from event_pipeline.sql now"; then
    apply_event_schema="true"
  fi
else
  echo "Event table 'share_events' already exists; skipping event_pipeline.sql by default."
  if confirm_default_no "Re-apply event_pipeline.sql anyway? This will usually fail on existing tables"; then
    apply_event_schema="true"
  fi
fi

if [[ "$apply_base_schema" == "true" || "$apply_event_schema" == "true" ]]; then
  tmp_sql="$(mktemp)"
  trap 'rm -f "$tmp_sql"' EXIT

  {
    printf 'SET ROLE "%s";\n\n' "$DB_USER"

    if [[ "$apply_base_schema" == "true" ]]; then
      strip_set_role "$CREATEDB_SQL"
      printf '\n'
    fi

    if [[ "$apply_event_schema" == "true" ]]; then
      strip_set_role "$EVENT_PIPELINE_SQL"
      printf '\n'
    fi
  } > "$tmp_sql"

  psql_admin -d "$DB_NAME" -f "$tmp_sql"
fi

echo
echo "Database setup complete."
echo "Use this connection string in sidecar configs:"
echo "Host=$PG_HOST;Port=$PG_PORT;Database=$DB_NAME;Username=$DB_USER;Password=<your-password>"
