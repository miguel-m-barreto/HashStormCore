#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG="${HASHSTORM_CONFIG:-$ROOT/configs/config.json}"
CONFIG_DIR="$ROOT/configs"
LOG_DIR="${HASHSTORM_LOG_DIR:-$ROOT/.hashstorm-run}"
BUILD_DIR="${HSC_BUILD_DIR:-$ROOT/build}"
TERMINAL_MODE="${HASHSTORM_TERMINALS:-true}"
TERMINAL_KEEP_OPEN="${HASHSTORM_TERMINAL_KEEP_OPEN:-true}"
START_REDIS_MODE="${HASHSTORM_START_REDIS:-auto}"

if [[ "${HASHSTORM_BACKGROUND:-false}" == "true" ]]; then
  TERMINAL_MODE="false"
fi

mkdir -p "$LOG_DIR"
cd "$ROOT"

is_running_pid_file() {
  local pid_file="$1"

  [[ -f "$pid_file" ]] || return 1

  local pid
  pid="$(cat "$pid_file" 2>/dev/null || true)"

  [[ "$pid" =~ ^[0-9]+$ ]] || return 1
  kill -0 "$pid" 2>/dev/null
}

start_process() {
  local name="$1"
  shift

  local pid_file="$LOG_DIR/$name.pid"
  local log_file="$LOG_DIR/$name.log"

  if is_running_pid_file "$pid_file"; then
    echo "$name is already running with PID $(cat "$pid_file"). Stop it first with scripts/stop-all.sh" >&2
    exit 1
  fi

  if should_open_terminals; then
    start_process_terminal "$name" "$pid_file" "$log_file" "$@"
  else
    echo "Starting $name ..."
    "$@" > "$log_file" 2>&1 &
    echo $! > "$pid_file"
  fi
}

resolve_published_command() {
  local name="$1"
  local executable="$2"
  local output_dir="$BUILD_DIR/$name"
  local native_path="$output_dir/$executable"
  local dll_path="$output_dir/$executable.dll"
  local legacy_native_path="$BUILD_DIR/$executable"
  local legacy_dll_path="$BUILD_DIR/$executable.dll"

  RESOLVED_COMMAND=()

  if [[ -x "$native_path" ]]; then
    RESOLVED_COMMAND=("$native_path")
    return
  fi

  if [[ -f "$dll_path" ]]; then
    RESOLVED_COMMAND=(dotnet "$dll_path")
    return
  fi

  if [[ "$name" == "pool-core" && -x "$legacy_native_path" ]]; then
    RESOLVED_COMMAND=("$legacy_native_path")
    return
  fi

  if [[ "$name" == "pool-core" && -f "$legacy_dll_path" ]]; then
    RESOLVED_COMMAND=(dotnet "$legacy_dll_path")
    return
  fi

  echo "$name has not been published yet." >&2
  echo "Expected one of:" >&2
  echo "  $native_path" >&2
  echo "  $dll_path" >&2
  if [[ "$name" == "pool-core" ]]; then
    echo "  $legacy_native_path" >&2
    echo "  $legacy_dll_path" >&2
  fi
  echo "Build first with:" >&2
  echo "  scripts/build-services.sh    # sidecars only" >&2
  echo "  scripts/build-all.sh         # Pool Core + sidecars" >&2
  exit 1
}

should_open_terminals() {
  case "$TERMINAL_MODE" in
    true)
      return 0
      ;;
    false)
      return 1
      ;;
    auto)
      [[ -t 1 && ( -n "${DISPLAY:-}" || -n "${WAYLAND_DISPLAY:-}" ) ]]
      ;;
    *)
      echo "Invalid HASHSTORM_TERMINALS value '$TERMINAL_MODE'. Use true, false, or auto." >&2
      exit 1
      ;;
  esac
}

open_terminal() {
  local title="$1"
  local command="$2"

  if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" ]]; then
    return 1
  fi

  if command -v gnome-terminal >/dev/null 2>&1; then
    gnome-terminal --title="$title" -- bash -lc "$command"
  elif command -v konsole >/dev/null 2>&1; then
    konsole --new-tab -p "tabtitle=$title" -e bash -lc "$command"
  elif command -v mate-terminal >/dev/null 2>&1; then
    mate-terminal --title="$title" -- bash -lc "$command"
  elif command -v x-terminal-emulator >/dev/null 2>&1; then
    x-terminal-emulator -T "$title" -e bash -lc "$command"
  elif command -v xterm >/dev/null 2>&1; then
    xterm -T "$title" -e bash -lc "$command"
  else
    return 1
  fi
}

start_process_terminal() {
  local name="$1"
  local pid_file="$2"
  local log_file="$3"
  shift 3

  local command_line=""
  local arg

  for arg in "$@"; do
    command_line+="$(printf '%q' "$arg") "
  done

  local terminal_script
  terminal_script="$(cat <<EOF
set -uo pipefail
cd $(printf '%q' "$ROOT")
: > $(printf '%q' "$log_file")
rm -f $(printf '%q' "$pid_file")
echo "=== HashStormCore $name ==="
echo "Log: $(printf '%q' "$log_file")"
echo
$command_line> >(tee -a $(printf '%q' "$log_file")) 2>&1 &
child=\$!
echo "\$child" > $(printf '%q' "$pid_file")
set +e
wait "\$child"
status=\$?
echo
echo "=== $name exited with status \$status ==="
if [[ $(printf '%q' "$TERMINAL_KEEP_OPEN") == "true" ]]; then
  read -r -p "Press Enter to close this terminal..."
fi
exit "\$status"
EOF
)"

  echo "Starting $name in a terminal ..."

  if ! open_terminal "HashStormCore $name" "$terminal_script"; then
    echo "Could not open a terminal for $name; falling back to background log mode." >&2
    "$@" > "$log_file" 2>&1 &
    echo $! > "$pid_file"
  fi
}

preflight_not_running() {
  local name="$1"
  local pid_file="$LOG_DIR/$name.pid"

  if is_running_pid_file "$pid_file"; then
    echo "$name is already running with PID $(cat "$pid_file"). Stop it first with scripts/stop-all.sh" >&2
    exit 1
  fi
}

warn_missing_sidecar_config() {
  local file="$1"
  local name="$2"

  if [[ ! -f "$file" ]]; then
    echo "$name config override '$file' was not found; deriving defaults from Pool Core config: $CONFIG"
  fi
}

redis_is_reachable() {
  command -v redis-cli >/dev/null 2>&1 && redis-cli ping >/dev/null 2>&1
}

wait_for_redis() {
  local timeout="${HASHSTORM_REDIS_START_TIMEOUT:-10}"

  for _ in $(seq 1 "$timeout"); do
    if redis_is_reachable; then
      return 0
    fi

    sleep 1
  done

  return 1
}

ensure_redis_if_requested() {
  case "$START_REDIS_MODE" in
    true|auto)
      if redis_is_reachable; then
        echo "Redis is already reachable"
        return
      fi

      if ! command -v redis-server >/dev/null 2>&1; then
        echo "Redis is not reachable and redis-server was not found in PATH." >&2
        echo "Install/start Redis before running the stack, or point configs to an external Redis instance." >&2
        exit 1
      fi

      echo "Redis is not reachable; starting local Redis with redis-server --daemonize yes ..."
      redis-server --daemonize yes

      if ! wait_for_redis; then
        echo "redis-server was started, but Redis did not become reachable before timeout." >&2
        exit 1
      fi
      ;;
    false)
      if ! redis_is_reachable; then
        echo "Redis is not reachable and HASHSTORM_START_REDIS=false." >&2
        echo "Start Redis manually or rerun without HASHSTORM_START_REDIS=false." >&2
        exit 1
      fi
      ;;
    *)
      echo "Invalid HASHSTORM_START_REDIS value '$START_REDIS_MODE'. Use true, false, or auto." >&2
      exit 1
      ;;
  esac
}

if [[ ! -f "$CONFIG" ]]; then
  echo "Pool Core config not found: $CONFIG" >&2
  echo "Create one with: cp configs/config.example.json configs/config.json" >&2
  exit 1
fi

export HASHSTORM_CONFIG="$CONFIG"

warn_missing_sidecar_config "$CONFIG_DIR/db-writer.json" "DbWriter"
warn_missing_sidecar_config "$CONFIG_DIR/live-aggregator.json" "LiveAggregator"
warn_missing_sidecar_config "$CONFIG_DIR/api-provider.json" "ApiProvider"

for name in db-writer live-aggregator api-provider pool-core; do
  preflight_not_running "$name"
done

ensure_redis_if_requested

resolve_published_command "db-writer" "HashStormCore.DbWriter"
db_writer_command=("${RESOLVED_COMMAND[@]}")

resolve_published_command "live-aggregator" "HashStormCore.LiveAggregator"
live_aggregator_command=("${RESOLVED_COMMAND[@]}")

resolve_published_command "api-provider" "HashStormCore.ApiProvider"
api_provider_command=("${RESOLVED_COMMAND[@]}")

resolve_published_command "pool-core" "HashStormCore"
pool_core_command=("${RESOLVED_COMMAND[@]}" -c "$CONFIG")

# Start consumers/API first so they are ready before Pool Core begins producing events.
start_process "db-writer" "${db_writer_command[@]}"
start_process "live-aggregator" "${live_aggregator_command[@]}"
start_process "api-provider" "${api_provider_command[@]}"
start_process "pool-core" "${pool_core_command[@]}"

sleep 2

echo
"$ROOT/scripts/status.sh"

echo
echo "Logs:"
echo "  $LOG_DIR/db-writer.log"
echo "  $LOG_DIR/live-aggregator.log"
echo "  $LOG_DIR/api-provider.log"
echo "  $LOG_DIR/pool-core.log"

failed="false"
for name in db-writer live-aggregator api-provider pool-core; do
  pid_file="$LOG_DIR/$name.pid"
  if ! is_running_pid_file "$pid_file"; then
    echo
    echo "$name exited early. Last log lines:"
    tail -n 80 "$LOG_DIR/$name.log" 2>/dev/null || true
    failed="true"
  fi
done

if [[ "$failed" == "true" ]]; then
  echo
  echo "One or more processes exited early. Fix the logs above before continuing." >&2
  exit 1
fi
