#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${HSC_CONFIGURATION:-Release}"
TFM="${HSC_TFM:-net10.0}"
BUILD_DIR="${HSC_BUILD_DIR:-$ROOT/build}"

publish_project() {
  local name="$1"
  local project="$2"
  local output="$BUILD_DIR/$name"

  echo "Publishing $name -> $output"
  rm -rf "$output"
  dotnet publish "$project" \
    -c "$CONFIGURATION" \
    --framework "$TFM" \
    -o "$output"
}

cd "$ROOT"
mkdir -p "$BUILD_DIR"

publish_project "pool-core" "$ROOT/src/HashStormCore/HashStormCore.csproj"
"$ROOT/scripts/build-services.sh"

echo
echo "Full publish complete."
echo "Output directory: $BUILD_DIR"
