# hippo

hippo is a command-line tool for a folder of markdown notes, such as a docs folder or a wiki. It indexes the folder into a local SQLite cache and answers questions about it from the terminal: which links are broken, what links to a page, which pages nothing links to, which pages have a given frontmatter value, and where a phrase appears.

## Install

On macOS and Linux:

```sh
curl -fsSL https://raw.githubusercontent.com/alisterpineda/hippo/main/install.sh | sh
```

On Windows, in PowerShell:

```powershell
irm https://raw.githubusercontent.com/alisterpineda/hippo/main/install.ps1 | iex
```

From 0.2.0, `hippo self update` updates it; on 0.1.0, rerun the script once. [Install](https://github.com/alisterpineda/hippo/blob/main/docs/install.md) covers installing a given version, updating, the .NET tool, installing by hand, Linux and Windows on arm64, and verifying a download.

## What it's for

In the folder that holds your notes, make it a workspace:

```console
$ hippo init
Created /Users/you/notes/.hippo/config.json. Edit it to choose which files are indexed, then run hippo index.
```

Every command brings the index up to date before it answers, so there is nothing to run between edits.

### Catch broken links

```console
$ hippo lint
index.md:5  broken-link  ideas.md -> ideas.md
meetings/2026-09-30.md:5  broken-link  ../projects/budget.md -> projects/budget.md
```

`lint` also reports frontmatter that fails to parse, and exits 1 when it reports anything, so it can fail a CI build. [Lint](https://github.com/alisterpineda/hippo/blob/main/docs/lint.md) lists the rules.

### See what links to a page before you move or delete it

```console
$ hippo backrefs reading-list.md
index.md:4            body         reading-list.md  [Reading list]
projects/garden.md:7  body         ../reading-list.md  [the reading list]
```

With `--transitive`, `backrefs` lists every file that reaches the page, directly or through other pages.

### Find pages nothing links to

```console
$ hippo find --no-backrefs
drafts/compost.md
index.md
```

### Query notes by their frontmatter

```console
$ hippo find --where status=active --field status,due
projects/garden.md  status=active  due=2026-11-01
reading-list.md  status=active
```

`--where` also compares, as in `'due<2026-12-01'`, and finds pages missing a field, as in `'!due'`. [Find](https://github.com/alisterpineda/hippo/blob/main/docs/find.md) covers every filter.

### Search the text, best match first

```console
$ hippo find 'raised beds'
meetings/2026-09-30.md  Garden kickoff
  Agreed on cedar for the raised beds. Budget is in [the plan](../projects/budget.md).
reading-list.md  Reading list
  - *The Well-Tempered Garden*, on planting for the long term - A guide to companion planting for raised beds
projects/garden.md  Garden redesign
  Raised beds along the south fence. See [the reading list](../reading-list.md) and the [kickoff notes](../meetings/2026-09...
```

### Give a script or an agent the same answers

Every command but `init` and `self update` prints JSON with `--json`, so a script, or an agent working in the same notes, can ask the same questions and read the answers:

```console
$ hippo backrefs reading-list.md --json
{
  "links": [
    {
      "source": "index.md",
      "line": 4,
      "kind": "body",
      "raw": "reading-list.md",
      "text": "Reading list"
    },
    ...
  ]
}
```

[Scripts and agents](https://github.com/alisterpineda/hippo/blob/main/docs/scripting.md) covers the exit codes and the shape of the JSON.

### Check OKF bundles

hippo checks [OKF v0.2](https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/main/SPEC.md) bundles against the format: the frontmatter each page must have, the index pages that list them, and the sources they cite. [OKF bundles](https://github.com/alisterpineda/hippo/blob/main/docs/okf.md) lists the rules.

## Commands

`hippo init`, `hippo cache` and `hippo self` aside, every command runs from anywhere inside a workspace: hippo walks up to the first folder with a `.hippo/config.json`.

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
| [`hippo self update [<version>]`](https://github.com/alisterpineda/hippo/blob/main/docs/install.md#updating) | Updates a hippo the install script put in place, to the latest release or to a named version, with the copy of the script built into it |

`hippo <command> --help` lists a command's options.

## Configuration

`.hippo/config.json` chooses which files are indexed, which folders are bundles, which frontmatter fields are links, which lint rules run, and how search matches words. A bundle is a folder whose pages treat it as their root, so a leading `/` in a link on one of them resolves against the bundle rather than the workspace root; a bundle whose root `index.md` declares `okf_version` is an OKF bundle. The starter `hippo init` writes indexes every file but those git ignores and the `.git`, `.obsidian` and `.trash` folders, and shows the other settings commented out. [docs/config.md](https://github.com/alisterpineda/hippo/blob/main/docs/config.md) describes every key.

## Documentation

- [Install](https://github.com/alisterpineda/hippo/blob/main/docs/install.md): the install scripts, the .NET tool, installing by hand, platform notes, and verifying a download
- [Configuration](https://github.com/alisterpineda/hippo/blob/main/docs/config.md): every key in `.hippo/config.json`, and how hippo uses `.gitignore`
- [Links](https://github.com/alisterpineda/hippo/blob/main/docs/links.md): bundles, how body and frontmatter links resolve, `refs` and `backrefs`
- [Find](https://github.com/alisterpineda/hippo/blob/main/docs/find.md): filters, frontmatter conditions and fields, and full-text search
- [Lint](https://github.com/alisterpineda/hippo/blob/main/docs/lint.md): the rules, turning them off, and leaving files out
- [OKF bundles](https://github.com/alisterpineda/hippo/blob/main/docs/okf.md): what makes a bundle OKF, and the rules it is checked against
- [Scripts and agents](https://github.com/alisterpineda/hippo/blob/main/docs/scripting.md): exit codes and the JSON each command prints
- [Cache](https://github.com/alisterpineda/hippo/blob/main/docs/cache.md): where the index lives, and cleaning up after moved workspaces
- [Known limits](https://github.com/alisterpineda/hippo/blob/main/docs/known-limits.md)
- [Changelog](https://github.com/alisterpineda/hippo/blob/main/CHANGELOG.md)

## License

[MIT](https://github.com/alisterpineda/hippo/blob/main/LICENSE)

## Development

Building, testing, migrations and releasing are covered in [docs/dev](https://github.com/alisterpineda/hippo/tree/main/docs/dev).
