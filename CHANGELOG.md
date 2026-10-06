# Changelog

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Nothing was released before 0.1.0 and its prereleases.

## Unreleased

### Added

- `hippo init` makes the current folder a workspace by writing a starter `.hippo/config.json`, which chooses the files indexed, the bundles, the frontmatter fields read as links, the lint settings and the search tokenizer.
- `hippo index` brings the workspace's index, a local SQLite cache, up to date; every workspace command does this first, and `--rebuild` re-reads every file.
- `hippo status` shows the workspace root, the database path, file counts and the last sweep.
- `hippo find` lists the indexed files, filtered by path glob, kind, frontmatter condition (`--where`) or links in and out (`--no-refs`, `--no-backrefs`), and shows frontmatter fields with `--field`.
- `hippo show` shows what the index holds for one file.
- `hippo refs` lists the links out of a file, body and frontmatter, and whether each target exists.
- `hippo backrefs` lists the links into a file or folder, filtered by link kind or source, and with `--transitive` every file that reaches it.
- `hippo lint` reports broken links, frontmatter that fails to parse and where OKF bundles depart from OKF v0.2, and exits 1 when there are findings; `lint.off` and `lint.exclude` in the config turn rules off and leave files out.
- `hippo cache list` lists every index in the cache with its state, size and workspace root.
- `hippo cache prune` removes the indexes of workspaces that were deleted, moved or renamed, with `--dry-run` and `--include-unreachable`.
- OKF v0.2 checks: a folder listed in `bundles` whose root `index.md` declares `okf_version` is an OKF bundle, its path fields are links, and `lint` checks it with the rules `okf-type`, `okf-index-frontmatter`, `okf-log-date`, `okf-source-resource`, `okf-footnote`, `okf-timestamp`, `okf-actor`, `okf-status` and `okf-index`; the three MUST rules cannot be turned off.
- Full-text search: `hippo find "<query>"` finds the pages holding every word and phrase of the query in their title, description, path or body, ranks them by BM25 and shows a snippet of each, with the `porter` tokenizer by default or `trigram` for substrings.
- `--json` on every command but `init` prints JSON instead of text.
- `hippo --version` prints the version and the commit it was built from, such as `0.1.0+d9d09f6`.
- Native AOT binaries for `osx-arm64`, `osx-x64`, `linux-x64` (glibc 2.38 or newer), `linux-musl-x64` and `win-x64`, as `dotnet tool` packages and as archives on the GitHub release, plus a framework-dependent `dotnet tool` package for other platforms; `dotnet tool install -g hippo` picks the right one.
