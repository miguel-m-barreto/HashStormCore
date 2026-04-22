#!/usr/bin/env bash
set -euo pipefail

LOG_FILE="${1:-build-libs.log}"
OUT_FILE="${2:-build-libs-diagnostics.log}"

awk '
function flush_block() {
  if (block != "") {
    print block ""
    block = ""
  }
}

{
  if ($0 ~ /(warning:|error:|fatal:|undefined reference|collect2: error|No such file|cannot find|failed|ld:|make(\[[0-9]+\])?: \*\*\*)/) {
    flush_block()
    capture = 1
    block = $0
    next
  }

  if (capture) {
    if (
      $0 ~ /^[[:space:]]*[0-9]+[[:space:]]+\|/ ||
      $0 ~ /^[[:space:]]*\|/ ||
      $0 ~ /^[[:space:]]*[\^~]+/ ||
      $0 ~ /note:/ ||
      $0 ~ /^In file included from/ ||
      $0 ~ /^[[:space:]]*from /
    ) {
      block = block ORS $0
      next
    }

    flush_block()
    capture = 0
  }
}

END {
  flush_block()
}
' "$LOG_FILE" > "$OUT_FILE"

echo "Wrote: $OUT_FILE"
