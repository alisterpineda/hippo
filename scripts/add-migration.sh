#!/usr/bin/env bash
# Adds an EF Core migration to Hippo.Migrations and exports that migration alone as the next numbered SQL script in
# src/Hippo/Migrations, named NNNN_<migration id>.sql. The number is the schema version the runner stores in
# user_version; the id pairs the script with its EF migration. The SQL is the real migration: review it before
# committing, and never edit a shipped script.
set -euo pipefail

name=${1:?usage: scripts/add-migration.sh <Name>}
cd "$(dirname "$0")/.."

project=src/Hippo.Migrations
scripts=src/Hippo/Migrations

latest_migration() {
  [ -d "$project/Migrations" ] || return 0
  find "$project/Migrations" -maxdepth 1 -name '[0-9]*_*.cs' ! -name '*.Designer.cs' \
    | sed -E 's|.*/([0-9]+_[^/]+)\.cs$|\1|' | sort | tail -n 1
}

dotnet tool restore >/dev/null
previous=$(latest_migration)
added=$(dotnet ef migrations add "$name" --project "$project" 2>&1) || { echo "$added" >&2; exit 1; }
echo "$added"
current=$(latest_migration)
if [ "$current" = "$previous" ]; then
  echo "No new migration was created." >&2
  exit 1
fi

mkdir -p "$scripts"
count=$(find "$scripts" -maxdepth 1 -name '[0-9][0-9][0-9][0-9]_*.sql' | wc -l | tr -d ' ')
output=$(printf '%s/%04d_%s.sql' "$scripts" $((count + 1)) "$current")
dotnet ef migrations script "${previous:-0}" "$current" --project "$project" --no-transactions -o "$output"
# hippo records the schema version in user_version, not EF's history table, and turns foreign keys off around the
# whole migration itself: SQLite ignores PRAGMA foreign_keys inside the runner's transaction. The BOM goes too.
perl -0pi -e '
  s/\A\x{EF}\x{BB}\x{BF}//;
  s/^(?:CREATE TABLE IF NOT EXISTS|INSERT INTO) "__EFMigrationsHistory".*?;\n\n?//gms;
  s/^PRAGMA foreign_keys = \d;\n\n?//gm;
  s/\n+\z/\n/;
' "$output"

echo "Wrote $output. Review it, then update the SQL and record types in src/Hippo that touch the changed tables."
if grep -q "may result in the loss of data" <<<"$added"; then
  echo >&2
  echo "WARNING: EF says this migration may lose data. Read every DROP in $output; if the loss is not intended," >&2
  echo "run 'dotnet ef migrations remove --project $project', delete $output, and reshape the change." >&2
fi
