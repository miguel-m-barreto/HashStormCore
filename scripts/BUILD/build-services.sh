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

publish_project "db-writer" "$ROOT/src/HashStormCore.DbWriter/HashStormCore.DbWriter.csproj"
publish_project "live-aggregator" "$ROOT/src/HashStormCore.LiveAggregator/HashStormCore.LiveAggregator.csproj"
publish_project "api-provider" "$ROOT/src/HashStormCore.ApiProvider/HashStormCore.ApiProvider.csproj"

echo
echo "Service publish complete."
echo "Output directory: $BUILD_DIR"
