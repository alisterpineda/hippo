# hippo

A standalone CLI that indexes a markdown notebook into a local SQLite cache and answers structural questions about it: what links where, what is broken, what breaks the notebook's conventions, and where a phrase appears.

Status: phase 1 (index files). hippo indexes every file in the notebook and the frontmatter of every markdown file.

## Commands

Run from anywhere inside a notebook: hippo walks up to the first folder with a `.hippo.yaml`. Every command brings the index up to date first, prints text by default and JSON with `--json`, and exits 0 on success and 2 on error.

```
hippo index [--rebuild]      bring the index up to date; --rebuild re-reads every file
hippo status                 notebook root, database path, file counts, last sweep
hippo files [--glob <pattern>] [--where <field>=<value>]
                             list indexed files, filtered by path or frontmatter value
hippo show <path>            what the index holds for one file
```

`.hippo.yaml` chooses which files are indexed:

```yaml
version: 1
files:
  include: ["**/*"]
  exclude: [".git/**", ".obsidian/**", ".trash/**"]
```

The index lives in `<user cache>/hippo/<hash of notebook root>/index.db`; set `HIPPO_CACHE_DIR` to an absolute path to replace `<user cache>/hippo`.

## Layout

```
src/Hippo/               the CLI (AOT-compatible; trim and AOT warnings are errors)
src/Hippo.Migrations/    dev-time only: EF Core model for authoring migrations; never shipped
tests/Hippo.Tests.Unit/  in-process tests of the CLI's code
tests/Hippo.Tests.E2E/   runs hippo as a separate process, as a user would
```

## Build and test

```sh
dotnet build -c Release
dotnet test -c Release
```

The E2E tests run the build output under the dotnet host. To run them against a published binary instead, set `HIPPO_EXE` to its absolute path:

```sh
dotnet publish src/Hippo -c Release -r osx-arm64 -p:PublishAot=true -o artifacts/publish/osx-arm64
HIPPO_EXE="$PWD/artifacts/publish/osx-arm64/hippo" dotnet test tests/Hippo.Tests.E2E -c Release
```

## Migrations

The schema is authored as EF Core entities in `src/Hippo.Migrations`. After changing them, run:

```sh
scripts/add-migration.sh <Name>
```

It adds the EF migration and exports it alone to `src/Hippo/Migrations/NNNN_<migration id>.sql`, such as `0001_20260928195655_Files.sql`, which hippo embeds and applies on start. The number is the schema version; the id pairs the script with its EF migration. Review the SQL; a shipped script is never edited.

## Distribution

Native AOT binaries are built per platform (`osx-arm64`, `osx-x64`, `linux-x64`, `win-x64`) with the publish command above. Native AOT cannot cross-compile between operating systems, so a Mac builds only the two macOS binaries; CI builds each binary on its own OS.

The `dotnet tool` package installs from a local feed. `local-feed.nuget.config` limits the install to that feed, since the `hippo` ID is not reserved on nuget.org:

```sh
dotnet pack src/Hippo -c Release -o artifacts/nupkg
dotnet tool install hippo --tool-path artifacts/tool --configfile local-feed.nuget.config \
  --version "$(dotnet msbuild src/Hippo -getProperty:Version)"
artifacts/tool/hippo
```
