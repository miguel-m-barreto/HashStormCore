#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG_DIR="${HASHSTORM_LOG_DIR:-$ROOT/.hashstorm-run}"
STOP_TIMEOUT="${HASHSTORM_STOP_TIMEOUT:-10}"

is_expected_process() {
  local name="$1"
  local pid="$2"
  local command

  command="$(ps -p "$pid" -o command= 2>/dev/null || true)"

  case "$name" in
    pool-core)
      [[ "$command" == *"src/HashStormCore/HashStormCore.csproj"* || "$command" == *"/HashStormCore.dll"* || "$command" == *"/HashStormCore "* || "$command" == *"/HashStormCore"$ ]]
      ;;
    db-writer)
      [[ "$command" == *"HashStormCore.DbWriter"* || "$command" == *"HashStormCore.DbWriter.csproj"* ]]
      ;;
    live-aggregator)
      [[ "$command" == *"HashStormCore.LiveAggregator"* || "$command" == *"HashStormCore.LiveAggregator.csproj"* ]]
      ;;
    api-provider)
      [[ "$command" == *"HashStormCore.ApiProvider"* || "$command" == *"HashStormCore.ApiProvider.csproj"* ]]
      ;;
    *)
      return 1
      ;;
  esac
}

stop_process() {
  local name="$1"
  local pid_file="$LOG_DIR/$name.pid"

  if [[ ! -f "$pid_file" ]]; then
    echo "$name: no pid file"
    return
  fi

  local pid
  pid="$(cat "$pid_file" 2>/dev/null || true)"

  if [[ ! "$pid" =~ ^[0-9]+$ ]]; then
    echo "$name: invalid pid file, removing"
    rm -f "$pid_file"
    return
  fi

  if ! kill -0 "$pid" 2>/dev/null; then
    echo "$name: not running, removing stale pid file"
    rm -f "$pid_file"
    return
  fi

  if ! is_expected_process "$name" "$pid"; then
    echo "$name: PID $pid does not look like a HashStormCore $name process; refusing to kill it"
    echo "      Remove stale pid file manually if needed: $pid_file"
    return
  fi

  echo "$name: stopping PID $pid"
  kill "$pid" 2>/dev/null || true

  for _ in $(seq 1 "$STOP_TIMEOUT"); do
    if ! kill -0 "$pid" 2>/dev/null; then
      rm -f "$pid_file"
      echo "$name: stopped"
      return
    fi
    sleep 1
  done

  echo "$name: did not stop after ${STOP_TIMEOUT}s; sending SIGKILL"
  kill -KILL "$pid" 2>/dev/null || true
  rm -f "$pid_file"
}

# Stop Pool Core first so it stops producing new events, then stop API/consumers.
for name in pool-core api-provider live-aggregator db-writer; do
  stop_process "$name"
done
