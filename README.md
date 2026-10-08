# hippo

hippo is a command-line tool for a folder of markdown notes, such as a docs folder or a wiki. It indexes the folder into a local SQLite cache and answers questions about it from the terminal: which links are broken, what links to a page, which pages nothing links to, which pages have a given frontmatter value, and where a phrase appears.

Every command but `init` prints JSON with `--json`, so a script or an agent working in the same notes can ask the same questions and read the answers.

hippo also checks [OKF v0.2](https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/main/SPEC.md) bundles against the format: the frontmatter each page must have, the index pages that list them, and the sources they cite.

## Install

On macOS and Linux:

```sh
curl -fsSL https://raw.githubusercontent.com/alisterpineda/hippo/main/install.sh | sh
```

On Windows, in PowerShell:

```powershell
irm https://raw.githubusercontent.com/alisterpineda/hippo/main/install.ps1 | iex
```

`install.sh` picks the native binary for your Mac or Linux x64 system (glibc or musl) from the latest release, checks it against the release's `SHA256SUMS`, and puts `hippo` in `~/.local/bin`; if that folder is not on your `PATH`, it prints the line to add to your shell's startup file. `install.ps1` does the same for Windows x64, puts `hippo.exe` in `%LOCALAPPDATA%\Programs\hippo`, and adds that folder to your user `PATH`, which terminals already open see once reopened. A download that does not match its checksum installs nothing, and neither script asks for sudo or administrator rights. Rerun the script to update.

`HIPPO_VERSION` installs a given version instead of the latest release, or a prerelease, which the latest release never is. `HIPPO_INSTALL_DIR` installs to another folder.

```sh
curl -fsSL https://raw.githubusercontent.com/alisterpineda/hippo/main/install.sh | HIPPO_VERSION=<version> sh
```

```powershell
$env:HIPPO_VERSION = '<version>'; irm https://raw.githubusercontent.com/alisterpineda/hippo/main/install.ps1 | iex; Remove-Item Env:HIPPO_VERSION
```

On Linux arm64 and Windows arm64, which have no native binary yet, the scripts stop and point at the .NET tool below. If another `hippo`, such as the .NET tool, is on your `PATH` ahead of the one installed, they warn that it runs first.

With the .NET SDK 10 or later, hippo also installs as a .NET tool:

```sh
dotnet tool install -g hippo               # the latest release
dotnet tool install -g hippo --prerelease  # the latest version, prereleases included
```

It installs a native binary on macOS, Linux x64 (glibc or musl) and Windows x64, and elsewhere a build that runs on the .NET runtime. `dotnet tool update -g hippo` upgrades it.

To install by hand, download the archive for your platform from the [GitHub Releases page](https://github.com/alisterpineda/hippo/releases), `hippo-<version>-<rid>.tar.gz`, or `hippo-<version>-win-x64.zip` for Windows, and put the `hippo` binary it holds (`hippo.exe` on Windows) on your `PATH`.

- **macOS:** the binaries are not notarized, so macOS blocks one downloaded through a browser on its first run. Clear the quarantine flag with `xattr -d com.apple.quarantine hippo`, or download the archive with `curl -LO` instead. Neither `curl` nor the install script sets the flag.
- **Linux x64:** the binary needs glibc 2.38 or newer, which Ubuntu 24.04, Debian 13, Fedora 39 and RHEL 10 have. On older distributions, such as Ubuntu 22.04, Debian 12 and RHEL 8 and 9, it does not start. The install script checks this before it downloads anything.

The install scripts check each archive against `SHA256SUMS`. For more than the checksum, GitHub attests every release's assets, and the [GitHub CLI](https://cli.github.com) checks that an archive is the one the release published:

```sh
gh release verify-asset -R alisterpineda/hippo v<version> hippo-<version>-<rid>.tar.gz
```

## Quickstart

In the folder that holds your notes, make it a workspace:

```console
$ hippo init
Created /Users/you/notes/.hippo/config.json. Edit it to choose which files are indexed, then run hippo index.
```

Every command brings the index up to date before it answers, so there is nothing to run between edits. Check for broken links:

```console
$ hippo lint
index.md:5  broken-link  ideas.md -> ideas.md
meetings/2026-09-30.md:5  broken-link  ../projects/budget.md -> projects/budget.md
```

Search the text of every page, best match first:

```console
$ hippo find 'raised beds'
meetings/2026-09-30.md  Garden kickoff
  Agreed on cedar for the raised beds. Budget is in [the plan](../projects/budget.md).
reading-list.md  Reading list
  - *The Well-Tempered Garden*, on planting for the long term - A guide to companion planting for raised beds
projects/garden.md  Garden redesign
  Raised beds along the south fence. See [the reading list](../reading-list.md) and the [kickoff notes](../meetings/2026-09...
```

See what links to a page:

```console
$ hippo backrefs reading-list.md
index.md:4            body         reading-list.md  [Reading list]
projects/garden.md:7  body         ../reading-list.md  [the reading list]
```

List the pages nothing links to:

```console
$ hippo find --no-backrefs
drafts/compost.md
index.md
```

Filter by frontmatter, and show the fields you care about:

```console
$ hippo find --where status=active --field status,due
projects/garden.md  status=active  due=2026-11-01
reading-list.md  status=active
```

## Commands

`hippo init` and `hippo cache` aside, every command runs from anywhere inside a workspace: hippo walks up to the first folder with a `.hippo/config.json`. Each prints text, or JSON with `--json` on every command but `init`, and exits 0 when clean, 1 when `lint` reports findings, and 2 on error. The JSON is always an object, and a command that lists things holds them under a key: `files` for `find` and `backrefs --transitive`, `links` for `refs` and `backrefs`, `findings` for `lint`, `indexes` for `cache list`, and `removed` for `cache prune`.

| Command | What it does |
| --- | --- |
| `hippo init` | Makes the current folder a workspace by writing a starter `.hippo/config.json`; it never overwrites one, and warns when the folder is already inside another workspace |
| `hippo index [--rebuild]` | Brings the index up to date; `--rebuild` re-reads every file |
| `hippo status` | Shows the workspace root, database path, file counts and last sweep |
| [`hippo find`](https://github.com/alisterpineda/hippo/blob/main/docs/find.md) | Lists the indexed files, filtered by path, kind, frontmatter or links, or with a query, searches their text |
| `hippo show <path>` | Shows what the index holds for one file |
| [`hippo refs <path>`](https://github.com/alisterpineda/hippo/blob/main/docs/links.md) | Lists the links out of a file, and whether each target exists |
| [`hippo backrefs <path>`](https://github.com/alisterpineda/hippo/blob/main/docs/links.md) | Lists the links into a file or folder, or with `--transitive`, every file that reaches it |
| [`hippo lint`](https://github.com/alisterpineda/hippo/blob/main/docs/lint.md) | Reports broken links, frontmatter that fails to parse, and where OKF bundles depart from OKF v0.2 |
| [`hippo cache list`](https://github.com/alisterpineda/hippo/blob/main/docs/cache.md) | Lists every index in the cache, with its state, size and workspace root |
| [`hippo cache prune`](https://github.com/alisterpineda/hippo/blob/main/docs/cache.md) | Removes the indexes of workspaces that were deleted, moved or renamed |

`hippo <command> --help` lists a command's options.

## Configuration

`.hippo/config.json` chooses which files are indexed, which folders are bundles, which frontmatter fields are links, which lint rules run, and how search matches words. A bundle is a folder whose pages treat it as their root, so a leading `/` in a link on one of them resolves against the bundle rather than the workspace root; a bundle whose root `index.md` declares `okf_version` is an OKF bundle. The starter `hippo init` writes indexes every file but those git ignores and the `.git`, `.obsidian` and `.trash` folders, and shows the other settings commented out. [docs/config.md](https://github.com/alisterpineda/hippo/blob/main/docs/config.md) describes every key.

## Documentation

- [Configuration](https://github.com/alisterpineda/hippo/blob/main/docs/config.md): every key in `.hippo/config.json`, and how hippo uses `.gitignore`
- [Links](https://github.com/alisterpineda/hippo/blob/main/docs/links.md): bundles, how body and frontmatter links resolve, `refs` and `backrefs`
- [Find](https://github.com/alisterpineda/hippo/blob/main/docs/find.md): filters, frontmatter conditions and fields, and full-text search
- [Lint](https://github.com/alisterpineda/hippo/blob/main/docs/lint.md): the rules, turning them off, and leaving files out
- [OKF bundles](https://github.com/alisterpineda/hippo/blob/main/docs/okf.md): what makes a bundle OKF, and the rules it is checked against
- [Cache](https://github.com/alisterpineda/hippo/blob/main/docs/cache.md): where the index lives, and cleaning up after moved workspaces
- [Known limits](https://github.com/alisterpineda/hippo/blob/main/docs/known-limits.md)
- [Changelog](https://github.com/alisterpineda/hippo/blob/main/CHANGELOG.md)

## License

[MIT](https://github.com/alisterpineda/hippo/blob/main/LICENSE)

## Development

Building, testing, migrations and releasing are covered in [docs/dev](https://github.com/alisterpineda/hippo/tree/main/docs/dev).
