# hippo

A standalone CLI that indexes a markdown notebook into a local SQLite cache and answers structural questions about it: what links where, what is broken, what breaks the notebook's conventions, and where a phrase appears.

Status: phase 2 (link graph). hippo indexes every file in the notebook, the frontmatter of every markdown file, and every link out of a markdown file.

## Commands

`hippo init` makes the current folder a notebook by writing a starter `.hippo.yaml`; it will not overwrite one, and warns when the folder is already inside another notebook. Every other command runs from anywhere inside a notebook: hippo walks up to the first folder with a `.hippo.yaml`. Each brings the index up to date first, prints text by default and JSON with `--json`, and exits 0 when clean, 1 when it reports findings (`broken`, `orphans`), and 2 on error.

```
hippo init                   write a starter .hippo.yaml in the current folder
hippo index [--rebuild]      bring the index up to date; --rebuild re-reads every file
hippo status                 notebook root, database path, file counts, last sweep
hippo files [--glob <pattern>] [--where <field>=<value>]
                             list indexed files, filtered by path or frontmatter value
hippo show <path>            what the index holds for one file
hippo refs <path>            the links out of a file: line, kind, and file, missing, url or anchor
hippo backrefs <path> [--kind body|frontmatter] [--transitive]
                             the links into a path; --transitive lists every file that reaches it
hippo broken                 links whose target is not an indexed file
hippo orphans                pages no other file links to, except the declared roots
```

`.hippo.yaml` chooses which files are indexed and how links resolve:

```yaml
files:
  include: ["**/*"]
  exclude: [".git/**", ".obsidian/**", ".trash/**"]
bundles:
  - root: wiki                   # a leading "/" in a link resolves against this folder
links:
  body: { resolve: page }        # page (the page's own folder) or bundle (its bundle root)
  wikilinks: text                # [[x]] is plain text, not a link
  frontmatter:
    - field: sources[].resource  # dotted for nested mappings; [] for each element of a list
      resolve: bundle
  roots: ["wiki/index.md"]       # pages that are not orphans without inbound links
```

Body links are CommonMark links, images, reference links and autolinks; their destinations are URLs, so percent-encoding is decoded and a query or fragment dropped. Frontmatter values are literal paths: nothing is decoded and a `?` is part of the path, but a `#` still starts a fragment that is dropped, so a frontmatter value cannot name a file with `#` in its name. A link with a scheme is a URL, one starting with `#` is anchor-only, and any other is a file when it resolves to an indexed file and missing otherwise.

The index lives in `<user cache>/hippo/<hash of notebook root>/index.db`; set `HIPPO_CACHE_DIR` to an absolute path to replace `<user cache>/hippo`.

## Known limits

- **Pathological markdown parses slowly.** A long run of text with no spaces or line breaks that holds many unclosed `[x](` takes time that grows with the square of its length: about 1 s at 50 KB and 14 s at 200 KB. Ordinary pages, even large ones full of links, parse in milliseconds. The cost is paid when the file changes, not on every command, and the answers stay correct. Minified code pasted outside a code block is the realistic way to hit it.
- **A page Markdig cannot parse has no body links.** Blocks nested past Markdig's depth limit make it give up on the page; hippo warns, keeps the page's frontmatter links, and indexes the rest of the notebook.
- **An unreadable page after a link-settings change slows every command.** Changing `bundles` or `links` makes hippo re-read every page to redo its links. If a page cannot be read then, hippo keeps its old links and does not record the new settings as applied, so each later command re-reads every page again, with a warning naming the file, until that page can be read. The answers stay correct; fixing the file's permissions ends it.
- **A same-size edit can hide on a skewed clock.** hippo re-reads a file whose mtime is within 2 s of when it was last read, which catches an edit made in the same mtime tick. On a filesystem whose clock is more than 2 s off this machine's, such as some network shares, such an edit can still be missed; `hippo index --rebuild` re-reads everything.
- **A file dated in the future is re-read on every command** until the clock passes its mtime, though its row is not rewritten.

## Layout

```
src/Hippo/               the CLI (AOT-compatible; trim and AOT warnings are errors)
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

The schema is a series of hand-written SQL scripts in `src/Hippo/Migrations`, named `NNNN_<name>.sql`, which hippo embeds and applies on start. The number is the schema version. To change the schema, add the next script:

- A change to a derived table (one the notebook can rebuild, such as `files`) drops and recreates the table in full. hippo reindexes after any script runs, so its rows need not survive.
- A new column is `NOT NULL` with no default unless it is genuinely optional.
- A shipped script is never edited; fixes go forward in a new one.

## Distribution

Native AOT binaries are built per platform (`osx-arm64`, `osx-x64`, `linux-x64`, `win-x64`) with the publish command above. Native AOT cannot cross-compile between operating systems, so a Mac builds only the two macOS binaries; CI builds each binary on its own OS.

The `dotnet tool` package installs from a local feed. `local-feed.nuget.config` limits the install to that feed, since the `hippo` ID is not reserved on nuget.org:

```sh
dotnet pack src/Hippo -c Release -o artifacts/nupkg
dotnet tool install hippo --tool-path artifacts/tool --configfile local-feed.nuget.config \
  --version "$(dotnet msbuild src/Hippo -getProperty:Version)"
artifacts/tool/hippo
```
