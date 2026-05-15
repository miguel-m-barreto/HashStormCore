#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MIGRATIONS_DIR="$ROOT/src/HashStormCore/Persistence/Postgres/Scripts/migrations"
MIN_SERVER_VERSION_NUM=160000
LOCK_KEY_1=1213416771
LOCK_KEY_2=20260515

DB_NAME="${HASHSTORM_DB_NAME:-hashstorm}"
DB_USER="${HASHSTORM_DB_USER:-hashstorm}"
PGPORT="${PGPORT:-5432}"
MIGRATION_CATEGORY="${HASHSTORM_MIGRATION_CATEGORY:-all}"
export PGPORT

LOCK_SESSION_PID=""
LOCK_INPUT_FD=""
LOCK_OUTPUT_FD=""
LOCK_ACQUIRED=0

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

validate_category() {
  local value="$1"

  if [[ ! "$value" =~ ^(all|schema|index)$ ]]; then
    echo "HASHSTORM_MIGRATION_CATEGORY must be one of: all, schema, index" >&2
    exit 1
  fi
}

psql_app() {
  psql -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" "$@"
}

lock_session_running() {
  local state

  state="$(ps -p "$LOCK_SESSION_PID" -o stat= 2>/dev/null || true)"
  [[ -n "$state" && "$state" != *Z* ]]
}

release_migration_lock() {
  if [[ -n "$LOCK_SESSION_PID" ]] && lock_session_running; then
    [[ -n "$LOCK_INPUT_FD" ]] && eval "exec ${LOCK_INPUT_FD}>&-" 2>/dev/null || true
    [[ -n "$LOCK_OUTPUT_FD" ]] && eval "exec ${LOCK_OUTPUT_FD}<&-" 2>/dev/null || true
    kill "$LOCK_SESSION_PID" 2>/dev/null || true
    wait "$LOCK_SESSION_PID" 2>/dev/null || true
  fi
}

cleanup_and_exit() {
  local status="$1"

  trap - EXIT INT TERM
  release_migration_lock
  exit "$status"
}

on_exit() {
  local status="$?"
  cleanup_and_exit "$status"
}

on_interrupt() {
  cleanup_and_exit 130
}

on_term() {
  cleanup_and_exit 143
}

acquire_migration_lock() {
  echo "Waiting for HashStormCore migration advisory lock ($LOCK_KEY_1, $LOCK_KEY_2)."

  coproc LOCK_PSQL {
    psql -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" -q -X -At
  }

  LOCK_SESSION_PID="$LOCK_PSQL_PID"
  LOCK_OUTPUT_FD="${LOCK_PSQL[0]}"
  LOCK_INPUT_FD="${LOCK_PSQL[1]}"
  trap on_exit EXIT
  trap on_interrupt INT
  trap on_term TERM

  printf 'SELECT pg_advisory_lock(%s, %s);\n' "$LOCK_KEY_1" "$LOCK_KEY_2" >&"$LOCK_INPUT_FD"
  printf "SELECT 'acquired';\n" >&"$LOCK_INPUT_FD"

  while IFS= read -r lock_result <&"$LOCK_OUTPUT_FD"; do
    if [[ "$lock_result" == "acquired" ]]; then
      LOCK_ACQUIRED=1
      echo "Acquired HashStormCore migration advisory lock."
      return
    fi
  done

  wait "$LOCK_SESSION_PID" 2>/dev/null || true
  echo "Failed to acquire HashStormCore migration advisory lock." >&2
  exit 1
}

assert_migration_lock_held() {
  if [[ "$LOCK_ACQUIRED" != "1" ]] || ! lock_session_running; then
    echo "HashStormCore migration advisory lock session ended unexpectedly. Refusing to continue." >&2
    exit 1
  fi
}

checksum_file() {
  local file="$1"

  sha256sum "$file" | awk '{print $1}'
}

validate_nontransactional_migration() {
  local file="$1"
  local filename="$2"

  echo "Nontransactional migration '$filename' must be restart-safe because it is recorded after successful SQL execution."

  if grep -Eiq 'CREATE[[:space:]]+INDEX[[:space:]]+CONCURRENTLY' "$file" &&
     ! grep -Eiq 'CREATE[[:space:]]+INDEX[[:space:]]+CONCURRENTLY[[:space:]]+IF[[:space:]]+NOT[[:space:]]+EXISTS' "$file"; then
    echo "Nontransactional migration '$filename' contains CREATE INDEX CONCURRENTLY without IF NOT EXISTS." >&2
    echo "Use CREATE INDEX CONCURRENTLY IF NOT EXISTS for restart-safe index migrations." >&2
    exit 1
  fi
}

migration_type_for_file() {
  local filename="$1"

  case "$filename" in
    *.tx.sql)
      printf '%s' "transactional"
      ;;
    *.ntx.sql)
      printf '%s' "nontransactional"
      ;;
    *)
      echo "Unsupported migration filename '$filename'. Expected *.tx.sql or *.ntx.sql." >&2
      exit 1
      ;;
  esac
}

migration_category_for_file() {
  local filename="$1"
  local stem="${filename%.sql}"
  stem="${stem%.tx}"
  stem="${stem%.ntx}"
  stem="${stem#*_}"
  printf '%s' "${stem%%_*}"
}

migration_id_for_file() {
  local filename="$1"
  printf '%s' "${filename%.sql}"
}

ensure_ledger() {
  psql_app <<'SQL'
CREATE TABLE IF NOT EXISTS public.hashstorm_schema_migrations
(
    migration_id TEXT NOT NULL PRIMARY KEY,
    migration_type TEXT NOT NULL,
    filename TEXT NOT NULL,
    checksum_sha256 TEXT NOT NULL,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    applied_by TEXT NOT NULL DEFAULT current_user,
    execution_seconds NUMERIC NULL
);
SQL
}

migration_status() {
  local migration_id="$1"
  local migration_type="$2"
  local filename="$3"
  local checksum="$4"

  psql_app -At \
    -v migration_id="$migration_id" \
    -v migration_type="$migration_type" \
    -v filename="$filename" \
    -v checksum_sha256="$checksum" <<'SQL'
SELECT CASE
    WHEN NOT EXISTS (
        SELECT 1
        FROM public.hashstorm_schema_migrations
        WHERE migration_id = :'migration_id'
    ) THEN 'pending'
    WHEN EXISTS (
        SELECT 1
        FROM public.hashstorm_schema_migrations
        WHERE migration_id = :'migration_id'
          AND migration_type = :'migration_type'
          AND filename = :'filename'
          AND checksum_sha256 = :'checksum_sha256'
    ) THEN 'applied'
    ELSE 'checksum_drift'
END;
SQL
}

record_migration() {
  local migration_id="$1"
  local migration_type="$2"
  local filename="$3"
  local checksum="$4"
  local execution_seconds="$5"

  psql_app \
    -v migration_id="$migration_id" \
    -v migration_type="$migration_type" \
    -v filename="$filename" \
    -v checksum_sha256="$checksum" \
    -v execution_seconds="$execution_seconds" <<'SQL'
INSERT INTO public.hashstorm_schema_migrations(migration_id, migration_type, filename, checksum_sha256, execution_seconds)
VALUES(:'migration_id', :'migration_type', :'filename', :'checksum_sha256', :'execution_seconds'::numeric);
SQL
}

apply_transactional_migration() {
  local file="$1"
  local migration_id="$2"
  local migration_type="$3"
  local filename="$4"
  local checksum="$5"
  local execution_seconds="$6"

  {
    printf 'BEGIN;\n'
    cat "$file"
    printf '\n'
    cat <<'SQL'
INSERT INTO public.hashstorm_schema_migrations(migration_id, migration_type, filename, checksum_sha256, execution_seconds)
VALUES(:'migration_id', :'migration_type', :'filename', :'checksum_sha256', :'execution_seconds'::numeric);
COMMIT;
SQL
  } | psql_app \
    -v migration_id="$migration_id" \
    -v migration_type="$migration_type" \
    -v filename="$filename" \
    -v checksum_sha256="$checksum" \
    -v execution_seconds="$execution_seconds"
}

validate_identifier "HASHSTORM_DB_NAME" "$DB_NAME"
validate_identifier "HASHSTORM_DB_USER" "$DB_USER"
validate_port "$PGPORT"
validate_category "$MIGRATION_CATEGORY"

if ! command -v sha256sum >/dev/null 2>&1; then
  echo "sha256sum is required to verify migration immutability." >&2
  exit 1
fi

if [[ ! -d "$MIGRATIONS_DIR" ]]; then
  echo "Migration directory not found: $MIGRATIONS_DIR" >&2
  exit 1
fi

server_version_num="$(psql_app -Atc "SELECT current_setting('server_version_num')::int")"
if (( server_version_num < MIN_SERVER_VERSION_NUM )); then
  echo "PostgreSQL 16 or newer is required. Server reported server_version_num=$server_version_num." >&2
  exit 1
fi

acquire_migration_lock
assert_migration_lock_held
ensure_ledger

mapfile -t migration_files < <(
  find "$MIGRATIONS_DIR" -maxdepth 1 -type f \( -name '*.tx.sql' -o -name '*.ntx.sql' \) -printf '%p\n' | sort
)

echo "Applying HashStormCore migrations to database '$DB_NAME' as user '$DB_USER'."
echo "PostgreSQL server_version_num=$server_version_num."
echo "Migration category: $MIGRATION_CATEGORY."

applied_count=0
skipped_count=0

for file in "${migration_files[@]}"; do
  assert_migration_lock_held

  filename="$(basename "$file")"

  if [[ ! "$filename" =~ ^[0-9]{3}_[a-z][a-z0-9_]*\.(tx|ntx)\.sql$ ]]; then
    echo "Invalid migration filename '$filename'. Expected NNN_category_description.tx.sql or NNN_category_description.ntx.sql." >&2
    exit 1
  fi

  migration_category="$(migration_category_for_file "$filename")"
  if [[ "$MIGRATION_CATEGORY" != "all" && "$migration_category" != "$MIGRATION_CATEGORY" ]]; then
    continue
  fi

  migration_id="$(migration_id_for_file "$filename")"
  migration_type="$(migration_type_for_file "$filename")"
  checksum="$(checksum_file "$file")"
  status="$(migration_status "$migration_id" "$migration_type" "$filename" "$checksum")"

  case "$status" in
    applied)
      echo "Skipped already-applied migration: $filename"
      skipped_count=$((skipped_count + 1))
      ;;
    checksum_drift)
      echo "Checksum drift detected for migration '$filename'. Refusing to continue." >&2
      exit 1
      ;;
    pending)
      echo "Applying $migration_type migration: $filename"
      start_seconds="$(date +%s)"

      if [[ "$migration_type" == "transactional" ]]; then
        apply_transactional_migration "$file" "$migration_id" "$migration_type" "$filename" "$checksum" "0"
        assert_migration_lock_held
      else
        validate_nontransactional_migration "$file" "$filename"
        psql_app -f "$file"
        assert_migration_lock_held
        end_seconds="$(date +%s)"
        execution_seconds=$((end_seconds - start_seconds))
        record_migration "$migration_id" "$migration_type" "$filename" "$checksum" "$execution_seconds"
        assert_migration_lock_held
      fi

      end_seconds="$(date +%s)"
      execution_seconds=$((end_seconds - start_seconds))

      if [[ "$migration_type" == "transactional" && "$execution_seconds" != "0" ]]; then
        psql_app \
          -v migration_id="$migration_id" \
          -v execution_seconds="$execution_seconds" <<'SQL'
UPDATE public.hashstorm_schema_migrations
SET execution_seconds = :'execution_seconds'::numeric
WHERE migration_id = :'migration_id';
SQL
      fi

      applied_count=$((applied_count + 1))
      ;;
    *)
      echo "Unexpected migration status '$status' for '$filename'." >&2
      exit 1
      ;;
  esac
done

if [[ "$MIGRATION_CATEGORY" != "all" && "$applied_count" == "0" && "$skipped_count" == "0" ]]; then
  echo "No migrations matched category '$MIGRATION_CATEGORY'."
fi

echo
echo "Migration run complete. Applied: $applied_count. Skipped: $skipped_count."
