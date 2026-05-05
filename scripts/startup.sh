#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG_DIR="$ROOT/configs"

prompt_default() {
  local prompt="$1"
  local default="$2"
  local value

  read -r -p "$prompt [$default]: " value
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

copy_if_missing() {
  local source="$1"
  local target="$2"
  local label="$3"

  if [[ -f "$target" ]]; then
    return
  fi

  if [[ ! -f "$source" ]]; then
    echo "$label source template not found: $source" >&2
    exit 1
  fi

  if confirm_default_yes "$label not found at '$target'. Create it from '$source'?"; then
    cp "$source" "$target"
    echo "Created $target"
  fi
}

default_pool_config() {
  if [[ -n "${HASHSTORM_CONFIG:-}" ]]; then
    printf '%s' "$HASHSTORM_CONFIG"
  elif [[ -f "$CONFIG_DIR/config.json" ]]; then
    printf '%s' "$CONFIG_DIR/config.json"
  elif [[ -f "$ROOT/config.json" ]]; then
    printf '%s' "$ROOT/config.json"
  elif [[ -f "$ROOT/src/HashStormCore/configs/config.json" ]]; then
    printf '%s' "$ROOT/src/HashStormCore/configs/config.json"
  elif [[ -f "$ROOT/src/HashStormCore/config.json" ]]; then
    printf '%s' "$ROOT/src/HashStormCore/config.json"
  else
    printf '%s' "$CONFIG_DIR/config.json"
  fi
}

warn_placeholders() {
  local file="$1"

  if [[ -f "$file" ]] && grep -qE "YOUR_|ADDRESS|Z-ADDRESS|YOUR_PASS|YOUR_NODE" "$file"; then
    echo
    echo "Warning: '$file' still appears to contain placeholders."
    echo "Edit it before running a real pool."
    grep -nE "YOUR_|ADDRESS|Z-ADDRESS|YOUR_PASS|YOUR_NODE" "$file" || true
  fi
}

validate_json_if_possible() {
  local file="$1"

  if [[ -f "$file" ]] && command -v python3 >/dev/null 2>&1; then
    python3 -m json.tool "$file" >/dev/null
  fi
}

echo "HashStormCore full local startup"
echo

mkdir -p "$CONFIG_DIR"

POOL_CONFIG="$(prompt_default "Pool Core config path" "$(default_pool_config)")"
copy_if_missing "$CONFIG_DIR/config.example.json" "$POOL_CONFIG" "Pool Core config"

copy_if_missing "$CONFIG_DIR/db-writer.example.json" "$CONFIG_DIR/db-writer.json" "DbWriter config override"
copy_if_missing "$CONFIG_DIR/live-aggregator.example.json" "$CONFIG_DIR/live-aggregator.json" "LiveAggregator config override"
copy_if_missing "$CONFIG_DIR/api-provider.example.json" "$CONFIG_DIR/api-provider.json" "ApiProvider config override"

validate_json_if_possible "$POOL_CONFIG"
validate_json_if_possible "$CONFIG_DIR/db-writer.json"
validate_json_if_possible "$CONFIG_DIR/live-aggregator.json"
validate_json_if_possible "$CONFIG_DIR/api-provider.json"

warn_placeholders "$POOL_CONFIG"
warn_placeholders "$CONFIG_DIR/db-writer.json"
warn_placeholders "$CONFIG_DIR/live-aggregator.json"
warn_placeholders "$CONFIG_DIR/api-provider.json"

LOG_DIR="$(prompt_default "Log/PID directory" "${HASHSTORM_LOG_DIR:-$ROOT/.hashstorm-run}")"

START_REDIS="true"
if ! confirm_default_yes "Start local Redis with redis-server --daemonize yes if Redis is not reachable"; then
  START_REDIS="false"
fi

BACKGROUND_MODE="false"
if confirm_default_no "Run processes in background instead of opening terminal windows"; then
  BACKGROUND_MODE="true"
fi

echo
echo "Startup summary:"
echo "  Pool config: $POOL_CONFIG"
echo "  DbWriter config override: $CONFIG_DIR/db-writer.json"
echo "  LiveAggregator config override: $CONFIG_DIR/live-aggregator.json"
echo "  ApiProvider config override: $CONFIG_DIR/api-provider.json"
echo "  Log directory: $LOG_DIR"
echo "  Start Redis: $START_REDIS"
echo "  Background mode: $BACKGROUND_MODE"
echo

if ! confirm_default_no "Continue and start all processes now"; then
  echo "Startup cancelled."
  exit 0
fi

export HASHSTORM_CONFIG="$POOL_CONFIG"
export HASHSTORM_LOG_DIR="$LOG_DIR"
export HASHSTORM_START_REDIS="$START_REDIS"
export HASHSTORM_BACKGROUND="$BACKGROUND_MODE"

"$ROOT/scripts/run-all.sh"
