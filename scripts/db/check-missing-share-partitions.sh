#!/usr/bin/env bash
set -euo pipefail

DB_NAME="${HASHSTORM_DB_NAME:-hashstorm}"
DB_USER="${HASHSTORM_DB_USER:-hashstorm}"
PGPORT="${PGPORT:-5432}"
CONFIG_PATH="${1:-}"
MIN_SERVER_VERSION_NUM=160000
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

  if [[ ! "$value" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$ ]]; then
    echo "Configured pool id '$value' must match ^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$" >&2
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

require_config() {
  local file="$1"

  if [[ -z "$file" ]]; then
    echo "Usage: bash scripts/db/check-missing-share-partitions.sh <config.json>" >&2
    exit 1
  fi

  if [[ ! -f "$file" ]]; then
    echo "Config file not found: $file" >&2
    exit 1
  fi
}

psql_app() {
  psql -v ON_ERROR_STOP=1 -U "$DB_USER" -d "$DB_NAME" "$@"
}

validate_identifier "HASHSTORM_DB_NAME" "$DB_NAME"
validate_identifier "HASHSTORM_DB_USER" "$DB_USER"
validate_port "$PGPORT"
require_config "$CONFIG_PATH"

server_version_num="$(psql_app -Atc "SELECT current_setting('server_version_num')::int")"
if (( server_version_num < MIN_SERVER_VERSION_NUM )); then
  echo "PostgreSQL 16 or newer is required. Server reported server_version_num=$server_version_num." >&2
  exit 1
fi

pool_output="$(python3 - "$CONFIG_PATH" <<'PY'
import json
import sys

config_path = sys.argv[1]

try:
    with open(config_path, "r", encoding="utf-8") as handle:
        data = json.load(handle)
except Exception as exc:
    print(f"Failed to parse config JSON: {exc}", file=sys.stderr)
    sys.exit(1)

pools = data.get("pools")
if not isinstance(pools, list):
    print("Config JSON must contain a top-level pools array.", file=sys.stderr)
    sys.exit(1)

for index, pool in enumerate(pools):
    if not isinstance(pool, dict):
        print(f"pools[{index}] must be an object.", file=sys.stderr)
        sys.exit(1)

    pool_id = pool.get("id")
    if not isinstance(pool_id, str) or not pool_id:
        print(f"pools[{index}].id must be a non-empty string.", file=sys.stderr)
        sys.exit(1)

    print(pool_id)
PY
)"

if [[ -n "$pool_output" ]]; then
  mapfile -t configured_pools <<<"$pool_output"
else
  configured_pools=()
fi

if (( ${#configured_pools[@]} == 0 )); then
  echo "No pools found in config: $CONFIG_PATH" >&2
  exit 1
fi

for pool_id in "${configured_pools[@]}"; do
  validate_pool_id "$pool_id"
done

shares_status="$(psql_app -At <<'SQL'
WITH shares_table AS (
    SELECT c.oid, c.relkind
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relname = 'shares'
      AND c.relkind IN ('r', 'p')
),
shares_partitioning AS (
    SELECT
        st.oid,
        st.relkind,
        pg_get_partkeydef(st.oid) AS partition_key,
        EXISTS (
            SELECT 1
            FROM pg_partitioned_table pt
            WHERE pt.partrelid = st.oid
              AND pt.partstrat = 'l'
              AND pg_get_partkeydef(st.oid) = 'LIST (poolid)'
        ) AS is_list_poolid
    FROM shares_table st
)
SELECT CASE
    WHEN NOT EXISTS (SELECT 1 FROM shares_table) THEN 'missing'
    WHEN EXISTS (SELECT 1 FROM shares_table WHERE relkind <> 'p') THEN 'not_partitioned'
    WHEN EXISTS (SELECT 1 FROM shares_partitioning WHERE is_list_poolid) THEN 'ok'
    ELSE 'wrong_partitioning'
END;
SQL
)"

case "$shares_status" in
  ok)
    ;;
  missing)
    echo "public.shares does not exist. Apply the HashStormCore schema before checking share partitions." >&2
    exit 1
    ;;
  not_partitioned)
    echo "public.shares exists but is not partitioned." >&2
    echo "This check is for partitioned installs only; migrating an existing non-partitioned shares table is a separate future task." >&2
    exit 1
    ;;
  wrong_partitioning)
    echo "public.shares is partitioned, but not by LIST (poolid)." >&2
    exit 1
    ;;
  *)
    echo "Unexpected shares partition status: $shares_status" >&2
    exit 1
    ;;
esac

existing_output="$(psql_app -At <<'SQL'
WITH shares_table AS (
    SELECT c.oid
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relname = 'shares'
      AND c.relkind = 'p'
),
partitions AS (
    SELECT pg_get_expr(child.relpartbound, child.oid) AS partition_bound
    FROM shares_table parent
    JOIN pg_inherits i ON i.inhparent = parent.oid
    JOIN pg_class child ON child.oid = i.inhrelid
)
SELECT pool_value
FROM (
    SELECT substring(partition_bound FROM $$FOR VALUES IN \('([^']*)'\)$$) AS pool_value
    FROM partitions
) parsed
WHERE pool_value IS NOT NULL
ORDER BY pool_value;
SQL
)"

declare -A existing_partitions=()
if [[ -n "$existing_output" ]]; then
  while IFS= read -r pool_id; do
    [[ -n "$pool_id" ]] && existing_partitions["$pool_id"]=1
  done <<<"$existing_output"
fi

missing=()
for pool_id in "${configured_pools[@]}"; do
  if [[ -z "${existing_partitions[$pool_id]:-}" ]]; then
    missing+=("$pool_id")
  fi
done

echo "Configured pools from $CONFIG_PATH:"
printf '  %s\n' "${configured_pools[@]}"

echo
echo "Existing public.shares partition pool values:"
if (( ${#existing_partitions[@]} == 0 )); then
  echo "  (none detected)"
else
  printf '%s\n' "${!existing_partitions[@]}" | sort | sed 's/^/  /'
fi

echo
if (( ${#missing[@]} > 0 )); then
  echo "Missing share partitions:"
  printf '  %s\n' "${missing[@]}"
  echo
  echo "Create each missing partition before starting or enabling writes for that pool."
  exit 1
fi

echo "All configured pools have public.shares partitions."
