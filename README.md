# hippo

A standalone CLI that indexes a markdown workspace into a local SQLite cache and answers structural questions about it: what links where, what is broken, what breaks the workspace's conventions, and where a phrase appears.

Status: phase 2 (link graph). hippo indexes every file in the workspace, the frontmatter of every markdown file, and every link out of a markdown file.

## Commands

`hippo init` makes the current folder a workspace by writing a starter `.hippo/config.json`; it will not overwrite one, and warns when the folder is already inside another workspace. `hippo cache` runs from anywhere and works on every index in the cache. Every other command runs from anywhere inside a workspace: hippo walks up to the first folder with a `.hippo/config.json`. Each brings the index up to date first, prints text by default and JSON with `--json`, and exits 0 when clean, 1 when it reports findings (`broken`, `orphans`), and 2 on error.

```
hippo init                   write a starter .hippo/config.json in the current folder
hippo index [--rebuild]      bring the index up to date; --rebuild re-reads every file
hippo status                 workspace root, database path, file counts, last sweep
hippo files [--glob <pattern>] [--where <field>=<value>]
                             list indexed files, filtered by path or frontmatter value
hippo show <path>            what the index holds for one file
hippo refs <path>            the links out of a file: line, kind, and file, missing, url or anchor
hippo backrefs <path> [--kind body|frontmatter] [--transitive]
                             the links into a path; --transitive lists every file that reaches it
hippo broken                 links whose target is not an indexed file
hippo orphans                pages with no link to or from another file
hippo cache list             every index in the cache: its state, size and workspace root
hippo cache prune [--dry-run] [--include-unreachable]
                             remove the indexes of workspaces that were deleted, moved or renamed
```

`.hippo/config.json` chooses which files are indexed and how links resolve. It is JSON that may hold `//` and `/* */` comments and trailing commas, and a key hippo does not know is an error:

```jsonc
{
  "files": {
    "include": ["**/*"],
    "exclude": [".git/**", ".obsidian/**", ".trash/**"],
    "gitignore": true                   // leave out the files git ignores, whatever include says
  },
  "links": {
    "bundles": ["wiki"],                // a leading "/" in a link on a page in wiki resolves against wiki
    "frontmatter": [
      {
        "field": "sources[].resource",  // dotted for nested mappings; [] for each element of a list
        "resolve": "bundle"             // page (the page's own folder, the default) or bundle (its bundle root)
      }
    ]
  }
}
```

`gitignore`, on unless set to `false`, leaves out every untracked file git ignores, as `git ls-files --others --ignored --exclude-standard` lists them from the workspace root: each `.gitignore`, `.git/info/exclude` and your global excludes file count, including rules in a repository that holds the workspace in a subfolder; when that repository ignores the workspace folder itself, nothing is left out. A tracked file is indexed even when a rule matches it, as git itself does. hippo runs git only when the workspace root or a folder above it holds a `.git`; if git is not on `PATH` or fails, hippo warns and indexes the ignored files.

A bundle is a folder whose pages treat it as their root: a leading `/` in a link resolves against the deepest bundle holding the page, or against the workspace root for a page in none. Other body links resolve from the page's folder; `[[x]]` is plain text, not a link. An orphan is a page with no link to or from another indexed file; links to itself, URLs, anchors and missing targets do not count.

Body links are CommonMark links, images, reference links and autolinks; their destinations are URLs, so percent-encoding is decoded and a query or fragment dropped. Frontmatter values are literal paths: nothing is decoded and a `?` is part of the path, but a `#` still starts a fragment that is dropped, so a frontmatter value cannot name a file with `#` in its name. A link with a scheme is a URL, one starting with `#` is anchor-only, and any other is a file when it resolves to an indexed file and missing otherwise.

The index lives in `<user cache>/hippo/<hash of workspace root>/index.db`; set `HIPPO_CACHE_DIR` to an absolute path to replace `<user cache>/hippo`. The hash is the SHA-256 of the root's path. On Windows that is the path the OS resolves the folder to, so a different letter case, an 8.3 short name, a junction or a `subst` drive still finds the same index. Each index records its root, which `hippo cache list` checks: `live` when the root still has a `.hippo/config.json`; `orphaned` when it does not but the root or the folder above it exists, as after the workspace was deleted, moved or renamed; `unreachable` when the drive the root was on is not mounted, or when neither the root nor the folder above it exists or they cannot be checked, as for an offline share; and `unknown` when the index records no root or cannot be read. `hippo cache prune` removes the orphaned indexes, and with `--include-unreachable` the unreachable ones too; nothing else removes an index. A moved workspace is indexed afresh at its new path, and its old index stays until pruned.

## Known limits

- **Pathological markdown parses slowly.** A long run of text with no spaces or line breaks that holds many unclosed `[x](` takes time that grows with the square of its length: about 1 s at 50 KB and 14 s at 200 KB. Ordinary pages, even large ones full of links, parse in milliseconds. The cost is paid when the file changes, not on every command, and the answers stay correct. Minified code pasted outside a code block is the realistic way to hit it.
- **A page Markdig cannot parse has no body links.** Blocks nested past Markdig's depth limit make it give up on the page; hippo warns, keeps the page's frontmatter links, and indexes the rest of the workspace.
- **An unreadable page after a link-settings change slows every command.** Changing `links` makes hippo re-read every page to redo its links. If a page cannot be read then, hippo keeps its old links and does not record the new settings as applied, so each later command re-reads every page again, with a warning naming the file, until that page can be read. The answers stay correct; fixing the file's permissions ends it.
- **A same-size edit can hide on a skewed clock.** hippo re-reads a file whose mtime is within 2 s of when it was last read, which catches an edit made in the same mtime tick. On a filesystem whose clock is more than 2 s off this machine's, such as some network shares, such an edit can still be missed; `hippo index --rebuild` re-reads everything.
- **A file dated in the future is re-read on every command** until the clock passes its mtime, though its row is not rewritten.
- **On macOS, a gitignored name with accented letters can still be indexed.** git reports names in composed Unicode form, but a name can be stored decomposed on disk, and hippo compares the two exactly. A file or folder whose name is stored that way is indexed even though git ignores it. An exclude pattern that matches it with `*` in place of the accented letters, such as `**/*.log`, leaves it out.

## Layout

```
src/Hippo/                      the CLI (AOT-compatible; trim and AOT warnings are errors)
src/Hippo.Migrations/           dev-time only: EF Core model for authoring migrations; never shipped
tests/Hippo.Tests.Unit/         one piece of the CLI at a time: parsing, queries, the sweep, migrations
tests/Hippo.Tests.Integration/  whole commands run in-process against a temp workspace
tests/Hippo.Tests.E2E/          hippo run as a separate process, as a user would
tests/Shared/                   helpers compiled into the unit and integration tests
```

A test of what a command does belongs in the integration tests, which pass each command its working directory, environment variables and clock. The E2E tests are kept for what only the real process shows: a smoke test per command, so every query and JSON shape runs once as native AOT compiled it; exit codes, arguments and output as the shell sees them; the user cache folder; and git, which hippo runs with its own environment.

## Build and test

```sh
dotnet build -c Release
dotnet test -c Release
```

The E2E tests run the build output under the dotnet host. To run them against a published binary instead, set `HIPPO_EXE` to its absolute path:

```sh
dotnet publish src/Hippo -c Release -r osx-arm64 -o artifacts/publish/osx-arm64
HIPPO_EXE="$PWD/artifacts/publish/osx-arm64/hippo" dotnet test tests/Hippo.Tests.E2E -c Release
```

## Migrations

The schema is authored as EF Core entities in `src/Hippo.Migrations`; hippo itself reads and writes with Dapper and never loads EF. After changing the entities, run:

```sh
scripts/add-migration.sh <Name>
```

It adds the EF migration and exports it alone to `src/Hippo/Migrations/NNNN_<migration id>.sql`, which hippo embeds and applies on start, all pending scripts in one transaction. The number is the schema version, kept in SQLite's `user_version`.

A migration keeps the data in the index:

- EF alters a table in place where SQLite can, and otherwise rebuilds it by copying every row into a new table. Review the SQL. When EF says a change may lose data, the script repeats the warning last; read every `DROP` it wrote.
- A new `NOT NULL` column needs a value for the rows already there. Choose it on purpose and declare it in the model with `HasDefaultValue`; `SchemaTests` fails until the model and the scripts build the same schema. hippo re-reads every file after a migration and rewrites each row in place, keeping its id, so a column derived from the file is filled then.
- `MigrationRunnerTests` migrates a populated index from the first schema through every script and fails when a row, or a value in a column the first schema had, is gone. It does not seed or check columns a later migration adds. A migration meant to discard data changes that test in the same commit.
- A shipped script is never edited; fixes go forward in a new migration.

## Distribution

Native AOT binaries are built per platform (`osx-arm64`, `osx-x64`, `linux-x64`, `linux-musl-x64`, `win-x64`) with the publish command above. Native AOT cannot cross-compile between operating systems, so a Mac builds only the two macOS binaries; CI builds each binary on its own OS, the musl one in an Alpine container. The `linux-x64` binary links against the glibc of the Ubuntu CI builds on and needs glibc 2.38 or later (Ubuntu 23.10, Debian 13, RHEL 10); CI fails if that rises. On an older glibc it does not start, and `dotnet tool install` picks it there all the same; building it against an older glibc would widen that.

Each binary is a single file with SQLite linked in. The publish downloads the SQLite amalgamation pinned in `src/Hippo/Sqlite.targets` from sqlite.org, checks its SHA-256, compiles it with the C compiler the native AOT link uses (clang or gcc, or Visual Studio's C++ tools on Windows), and leaves out the `e_sqlite3` library the SQLitePCLRaw package ships. Every other build, the tests and the framework-dependent tool package included, still loads that package library, so the two must be the same SQLite version; a unit test fails when they differ. The package decides the version: SQLitePCLRaw.bundle_e_sqlite3, which Microsoft.Data.Sqlite brings in. To move to a new SQLite, move that package, then update the version, URL and SHA-256 in `Sqlite.targets` to match; the version's [release log](https://sqlite.org/changes.html) gives the SHA3-256 of its `sqlite3.c` to check the download against.

hippo also ships as a `dotnet tool`, in one package per native binary plus a framework-dependent package for every other platform, which loads SQLite from the SQLitePCLRaw package. Users install the `hippo` package, which lists the others; the .NET CLI (SDK 10 or later) picks the native one for their platform and falls back to the framework-dependent one only where there is none. Each native package is packed on its own OS, like the binary:

```sh
dotnet pack src/Hippo -c Release -r osx-arm64 -o artifacts/nupkg               # a native package
dotnet pack src/Hippo -c Release -r any -p:PublishAot=false -o artifacts/nupkg  # the framework-dependent package
dotnet pack src/Hippo -c Release -o artifacts/nupkg                             # the package users install
```

Every package carries the same version. When publishing to a feed, push the package users install last, since installing it fails until the one it picks is there. CI uploads them all as the `tool-package` artifact.

To install from the local feed, use `local-feed.nuget.config`. The `hippo` IDs are not reserved on nuget.org, so it maps them to the local feed alone:

```sh
dotnet tool install hippo --tool-path artifacts/tool --configfile local-feed.nuget.config \
  --version "$(dotnet msbuild src/Hippo -getProperty:Version)"
artifacts/tool/hippo
```

The feed needs the package users install and the one for this platform. A leftover `bin/Release/net10.0/<rid>/publish/` folder is packed as it is, stale files included, so delete it before packing on a machine that published before.
