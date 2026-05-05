#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG_DIR="${HASHSTORM_LOG_DIR:-$ROOT/.hashstorm-run}"

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

for name in pool-core live-aggregator db-writer api-provider; do
  pid_file="$LOG_DIR/$name.pid"

  if [[ ! -f "$pid_file" ]]; then
    echo "$name: stopped"
    continue
  fi

  pid="$(cat "$pid_file" 2>/dev/null || true)"

  if [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null && is_expected_process "$name" "$pid"; then
    echo "$name: running ($pid)"
  elif [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null; then
    echo "$name: stale pid file points to another process ($pid)"
  else
    echo "$name: stopped"
  fi
done

echo
echo "Log directory: $LOG_DIR"
