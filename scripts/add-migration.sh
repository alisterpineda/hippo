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

previous=$(latest_migration)
dotnet ef migrations add "$name" --project "$project"
current=$(latest_migration)
if [ "$current" = "$previous" ]; then
  echo "No new migration was created." >&2
  exit 1
fi

mkdir -p "$scripts"
count=$(find "$scripts" -maxdepth 1 -name '[0-9][0-9][0-9][0-9]_*.sql' | wc -l | tr -d ' ')
output=$(printf '%s/%04d_%s.sql' "$scripts" $((count + 1)) "$current")
dotnet ef migrations script "${previous:-0}" "$current" --project "$project" --no-transactions -o "$output"
echo "Wrote $output. Review it, then update the SQL and record types in src/Hippo that touch the changed tables."
