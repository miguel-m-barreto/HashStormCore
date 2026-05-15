#!/usr/bin/env bash
set -euo pipefail

MIN_SERVER_VERSION_NUM=160000

DB_NAME="${HASHSTORM_DB_NAME:-hashstorm}"
DB_USER="${HASHSTORM_DB_USER:-hashstorm}"
DB_PASSWORD="${HASHSTORM_DB_PASSWORD:-}"
SKIP_PASSWORD_UPDATE="${SKIP_PASSWORD_UPDATE:-0}"
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

psql_admin() {
  psql -v ON_ERROR_STOP=1 "$@"
}

validate_identifier "HASHSTORM_DB_NAME" "$DB_NAME"
validate_identifier "HASHSTORM_DB_USER" "$DB_USER"
validate_port "$PGPORT"

server_version_num="$(psql_admin -d postgres -Atc "SELECT current_setting('server_version_num')::int")"
if (( server_version_num < MIN_SERVER_VERSION_NUM )); then
  echo "PostgreSQL 16 or newer is required. Server reported server_version_num=$server_version_num." >&2
  exit 1
fi

role_exists="$(psql_admin -d postgres -At -v db_user="$DB_USER" -c "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'db_user')")"
if [[ "$role_exists" != "t" || "$SKIP_PASSWORD_UPDATE" != "1" ]]; then
  if [[ -z "$DB_PASSWORD" ]]; then
    echo "HASHSTORM_DB_PASSWORD is required unless the role already exists and SKIP_PASSWORD_UPDATE=1." >&2
    exit 1
  fi
fi

echo "Preparing PostgreSQL database '$DB_NAME' and application role '$DB_USER'."
echo "PostgreSQL server_version_num=$server_version_num."

if [[ "$role_exists" == "t" ]]; then
  echo "Application role '$DB_USER' already exists."
else
  psql_admin -d postgres -v db_user="$DB_USER" -v db_password="$DB_PASSWORD" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'db_user', :'db_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'db_user')
\gexec
SQL
  echo "Created application role '$DB_USER'."
fi

if [[ "$role_exists" == "t" && "$SKIP_PASSWORD_UPDATE" == "1" ]]; then
  echo "Skipped password update for existing role '$DB_USER'."
else
  psql_admin -d postgres -v db_user="$DB_USER" -v db_password="$DB_PASSWORD" <<'SQL'
SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L', :'db_user', :'db_password')
WHERE EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'db_user')
\gexec
SQL
  echo "Updated password for application role '$DB_USER'."
fi

database_exists="$(psql_admin -d postgres -At -v db_name="$DB_NAME" -c "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db_name')")"
if [[ "$database_exists" == "t" ]]; then
  echo "Database '$DB_NAME' already exists."
else
  psql_admin -d postgres -v db_name="$DB_NAME" -v db_user="$DB_USER" <<'SQL'
SELECT format('CREATE DATABASE %I OWNER %I', :'db_name', :'db_user')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'db_name')
\gexec
SQL
  echo "Created database '$DB_NAME'."
fi

psql_admin -d postgres -v db_name="$DB_NAME" -v db_user="$DB_USER" <<'SQL'
SELECT format('GRANT ALL PRIVILEGES ON DATABASE %I TO %I', :'db_name', :'db_user')
\gexec
SQL

psql_admin -d "$DB_NAME" -v db_user="$DB_USER" <<'SQL'
SELECT format('GRANT USAGE, CREATE ON SCHEMA public TO %I', :'db_user')
\gexec
SQL

echo
echo "Database foundation is ready."
echo "Next step: connect as '$DB_USER' and apply the schema with:"
echo "  HASHSTORM_DB_NAME=$DB_NAME HASHSTORM_DB_USER=$DB_USER PGPORT=$PGPORT bash scripts/db/apply-schema.sh"
