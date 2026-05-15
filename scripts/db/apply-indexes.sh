#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

echo "Applying HashStormCore index migrations."
echo "This applies index-category migrations, including ApiProvider historical-read indexes when present."
echo "Future CREATE INDEX CONCURRENTLY migrations must use the .ntx.sql suffix."

HASHSTORM_MIGRATION_CATEGORY=index bash "$ROOT/scripts/db/apply-migrations.sh"
