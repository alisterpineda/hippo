# hippo

A standalone CLI that indexes a markdown workspace into a local SQLite cache and answers structural questions about it: what links where, what is broken, where a bundle departs from the standard it follows, and where a phrase appears.

Status: phase 5 (full-text search). hippo indexes every file in the workspace, the frontmatter of every markdown file, every link out of a markdown file, and the text of every markdown file; it checks [OKF v0.2](https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/main/SPEC.md) bundles against the format, and finds pages by what they say.

## Commands

`hippo init` makes the current folder a workspace by writing a starter `.hippo/config.json`; it will not overwrite one, and warns when the folder is already inside another workspace. `hippo cache` runs from anywhere and works on every index in the cache. Every other command runs from anywhere inside a workspace: hippo walks up to the first folder with a `.hippo/config.json`. Each brings the index up to date first, prints text by default and JSON with `--json`, and exits 0 when clean, 1 when it reports findings (`lint`), and 2 on error.

```
hippo init                   write a starter .hippo/config.json in the current folder
hippo index [--rebuild]      bring the index up to date; --rebuild re-reads every file
hippo status                 workspace root, database path, file counts, last sweep
hippo find ["<query>"] [--glob <pattern>…] [--kind markdown|other] [--where <condition>…]
           [--field <path>[,<path>…]…] [--errors] [--no-refs] [--no-backrefs] [--from <pattern>…]
           [--link-kind body|frontmatter] [--limit n]
                             every indexed file in path order, or with a query, the pages holding every
                             word of it, best match first, each with its title and a snippet; --glob,
                             --kind and --where filter by path, kind or frontmatter field, and a --glob
                             starting with ! leaves out what it matches; --where takes field=value,
                             field!=value, field<value, field<=value, field>value, field>=value, field or
                             !field, quoted for the shell ('as_of<2026-04-01'), keeping only pages, and
                             a value @field names another field of the page; a field path's part
                             ending in [] means each element of that list; --field shows those
                             frontmatter fields of each file; --errors keeps only files whose
                             frontmatter failed to parse, with the error; --no-refs keeps files with no
                             link to another file, and --no-backrefs those no other file links to;
                             --from counts only links from files it matches toward --no-backrefs, and
                             --link-kind only links of that kind toward either; --limit caps the
                             results (20 by default with a query, none without)
hippo show <path>            what the index holds for one file
hippo refs <path>            the links out of a file: line, kind, file, directory, missing, url or anchor,
                             and the link text
hippo backrefs <path> [--link-kind body|frontmatter] [--from <pattern>…] [--transitive]
                             the links into a path, with their text; --link-kind and --from keep only
                             links of that kind and from the files a glob matches; --transitive lists
                             every file that reaches it, along only such links
hippo lint [--rule <name>…]  where the workspace breaks a lint rule: links whose target is not an indexed
                             file or folder, frontmatter that fails to parse, and where OKF bundles depart
                             from OKF v0.2; --rule lists only the rules named, even one lint.off turns off
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
  "bundles": ["wiki"],                  // a leading "/" in a link on a page in wiki resolves against wiki
  "links": {
    "frontmatter": [                    // an OKF bundle's path fields, such as sources[].resource, need no entry
      {
        "field": "related[]",           // dotted for nested mappings; [] for each element of a list
        "resolve": "page"               // page (the page's own folder, the default) or bundle (its bundle root)
      }
    ]
  },
  "lint": {
    "off": ["okf-footnote"],            // rules to leave unchecked; OKF MUST rules are always checked
    "exclude": ["archive/**"]           // files whose findings lint leaves out; they stay indexed and linkable
  },
  "search": {
    "tokenizer": "porter"               // porter (whole words and their forms, the default) or trigram (any substring)
  }
}
```

`gitignore`, on unless set to `false`, leaves out every untracked file git ignores, as `git ls-files --others --ignored --exclude-standard` lists them from the workspace root: each `.gitignore`, `.git/info/exclude` and your global excludes file count, including rules in a repository that holds the workspace in a subfolder; when that repository ignores the workspace folder itself, nothing is left out. A tracked file is indexed even when a rule matches it, as git itself does. hippo runs git only when the workspace root or a folder above it holds a `.git`; if git is not on `PATH` or fails, hippo warns and indexes the ignored files.

The workspace root's `.hippo` folder is never indexed, whatever `include` says: it is hippo's, not the workspace's, so a link into it is broken. A nested workspace's `.hippo` folder is indexed like any other.

A bundle is a folder listed in `bundles` whose pages treat it as their root: a leading `/` in a link resolves against the deepest bundle holding the page, or against the workspace root for a page in none. Other body links resolve from the page's folder; `[[x]]` is plain text, not a link.

Body links are CommonMark links, images, reference links and autolinks; their destinations are URLs, so percent-encoding is decoded and a query or fragment dropped. Frontmatter values are literal paths: nothing is decoded and a `?` is part of the path, but a `#` still starts a fragment that is dropped, so a frontmatter value cannot name a file with `#` in its name. A link with a scheme is a URL, one starting with `#` is anchor-only, and any other is a file when it resolves to an indexed file, a directory when it resolves to a folder holding an indexed file, and missing otherwise. Paths are compared under Unicode canonical equivalence (NFC), on every OS: a link, or a path or glob given to a command, that spells `é` as `e` and a combining accent reaches a file whose name has the precomposed `é`, and the other way round. A file is always shown under its name on disk and a link as written. Where a filesystem keeps two such names apart as two files, a link to either reaches both. A body link's text is what a reader sees as the link, as plain text on one line, read as a page's title is: `[**Okf** bundles](x.md)` reads `Okf bundles`, an image gives its alt text, a reference link its label and an autolink its URL. `refs` and `backrefs` end each line with it in brackets, and a frontmatter link has none. A trailing `/` makes no difference, and the workspace root is a directory. A folder holding no indexed file, because it is empty or everything in it is excluded, is missing. `backrefs` of a folder lists the links to the folder itself, and `backrefs .` from the root lists those to the root. With `--transitive`, `--link-kind` and `--from` apply at every hop: a chain follows only links of that kind and passes only through files `--from` matches, so with `--from 'wiki/**'`, a wiki page that reaches a file only through a day entry is not listed.

### OKF bundles

A bundle is an [OKF v0.2](https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/main/SPEC.md) bundle when its root `index.md` declares `okf_version` in its frontmatter. hippo reads every OKF bundle as 0.2; `lint` notes one that declares another version. A bundle without the declaration is a plain bundle, and a folder not listed in `bundles` is never an OKF bundle, whatever its `index.md` says. A page belongs to the deepest bundle holding it.

In an OKF bundle, OKF's path fields are frontmatter links with no `links.frontmatter` entry: `resource`, `sources[].resource`, `computation`, `executor.resource` and `attester.resource`. A relative value resolves against the bundle root, as the spec's examples do; a `links.frontmatter` entry for the same field resolves it as the entry says instead. A path may leave its bundle. Every `sources[].resource` that is not a URL is read as a path, so a scope descriptor such as `all queries in BigQuery project X` is a broken link. Body links resolve as they do anywhere else.

### Lint

`hippo lint` checks the workspace against the rules below, lists the findings by path and line, and exits 1 when there are any. Every rule is on unless `lint.off` turns it off, and `--rule` checks only the rules it names, even one `lint.off` turns off. The workspace rules check every indexed file. The OKF rules check OKF bundles, and only when one of them is checked does `lint` note that no bundle declares `okf_version`, or that one declares a version other than 0.2.

`lint.exclude` takes globs in the same syntax as `files.exclude`, and `lint` leaves out every finding on a file one of them matches, whatever `--rule` names; the findings left out do not count toward the exit status. A glob matches the file the finding is on, not a link's target, so a broken link from another page into an excluded folder is still reported, while the findings on the files in that folder, its own bundle's OKF findings included, are not. An excluded file stays indexed and is still a link target, unlike one `files.exclude` leaves out, which suits frozen content such as an imported archive. The findings are left out when `lint` runs, so changing `lint.exclude` needs no reindex. Nor does the sweep warn about frontmatter or links that fail to parse in an excluded file; it still warns about one it cannot read at all.

#### Workspace rules

| Rule | `lint.off` | Reports |
| --- | --- | --- |
| `broken-link` | Yes | A path link whose target is neither an indexed file nor a folder holding one, or that leaves the workspace |
| `frontmatter-syntax` | Yes | A markdown file whose frontmatter fails to parse |

A `broken-link` finding is on the line of the link, gives the link as written and the path it resolves to, and relates that path. It is worked out when `lint` runs, from the links the index holds, so a target that comes or goes changes it without its linking pages being read again.

A `frontmatter-syntax` finding is on the whole file and gives the parse error; it reports the files `hippo find --errors` lists, less those `lint.exclude` matches. It checks only that the frontmatter parses, not what it holds, and it reports a concept in an OKF bundle as well as `okf-type` does, so turning it off leaves the OKF finding in place.

#### OKF rules

The OKF rules check every markdown file in every OKF bundle. `index.md` and `log.md` are reserved at every level and checked for their own structure; every other markdown file is a concept.

A rule marked *No* is a MUST rule in OKF v0.2: a bundle that breaks it does not conform, so `lint` always checks it.

| Rule | `lint.off` | Reports |
| --- | --- | --- |
| `okf-type` | No | A concept with no frontmatter, frontmatter that fails to parse, or no non-empty `type` |
| `okf-index-frontmatter` | No | Frontmatter in an `index.md` below the bundle root, or a key other than `okf_version` in the root one |
| `okf-log-date` | No | A level-2 heading in a `log.md` that is not a `YYYY-MM-DD` date |
| `okf-source-resource` | Yes | A `sources` entry with no `resource` |
| `okf-footnote` | Yes | A footnote whose label matches no `sources[].id` on its page, explanatory footnotes included. A definition nothing references is not checked |
| `okf-timestamp` | Yes | A value in `generated.at`, `verified[].at`, `stale_after`, `sources[].last_modified` or a `usage_window` that is not an ISO 8601 datetime with an explicit UTC offset |
| `okf-actor` | Yes | A `generated` with no `by`, or a `generated.by` or `verified[].by` not shaped `<producer>/<version>`, `human:<id>` or `process:<id>`. A bare `verified` mapping counts as a one-element list |
| `okf-status` | Yes | A `status` other than `draft`, `stable` or `deprecated` |
| `okf-index` | Yes | An `index.md` entry whose page is missing or whose description is not the page's `description`, or a page that no `index.md` above it links to |

Every OKF rule is on for every OKF bundle. `lint.off` turns off the rules marked *Yes*; naming one marked *No* there is a config error, so a run with no findings from those means the bundle is conformant. A concept whose frontmatter fails to parse is reported by `okf-type` alone among the OKF rules that read frontmatter, since the rest cannot be checked without it; `okf-index` still checks that an index lists it. Findings are made when a page is indexed and kept in the index, so `lint` costs no more than a query.

`okf-index` reads an entry as a list item that opens with a link, `* [Title](url) - description`. The description is the text after the link and its separator (a hyphen, dash or colon) as written, with each run of whitespace read as one space, and it must equal the page's `description`, read the same way. An entry for a folder, an `index.md` or `log.md`, a file that is not markdown, or a page whose frontmatter fails to parse has no description to compare. An `index.md` covers the pages in its own folder and every folder below it, up to its bundle's root, and a page is listed when any `index.md` covering it links to it, in an entry or anywhere else. Every OKF bundle has a root `index.md`, so every page in it must be linked from one; a bundle that keeps no listing can turn the rule off. Each `okf-index` finding concerns an index and a page that can change apart, so it is worked out when `lint` runs, from what the index holds.

### Find

Without a query, `hippo find` lists every indexed file in path order, one path per line, and under `--errors` each path followed by its frontmatter error. With `--json`, each file has its path, kind, size, modified time, title (null for a file that is not a page, or a page with none), parse error and snippet (null without a query), and under `--field`, its fields. The filters keep only the files they match, and `--limit` counts what is left; without a query there is no limit unless one is given.

`--glob` takes a glob relative to the workspace root, and can be given more than once: a file is kept when any glob matches it, less those a glob starting with `!` matches. When every glob starts with `!`, they leave out what they match from every file, so `--glob '!archive/**'` keeps everything outside `archive`. `--kind` keeps only `markdown` files, those whose names end in `.md`, or only `other` files. `--no-backrefs` keeps the files, markdown or not, that no other file links to, and `--no-refs` those with no link to another indexed file; a file's links to itself, and links to folders, URLs, anchors and missing targets, count for neither. Together, `--no-refs --no-backrefs` list the files with no link in or out. The link filters read every link in the index, so a file `--glob` leaves out still has its links count.

`--from` makes `--no-backrefs` count only the links whose source file matches it, and `--link-kind body` or `--link-kind frontmatter` makes `--no-refs` and `--no-backrefs` count only links of that kind. `--from` takes globs as `--glob` does: relative to the workspace root, repeatable, and a leading `!` leaves files out. A file's links to itself still never count. Each needs a filter to narrow, so `--from` without `--no-backrefs`, or `--link-kind` without `--no-refs` or `--no-backrefs`, is an error. To list the artifacts no wiki page cites in its frontmatter, however many day entries link them:

```sh
hippo find --glob 'raw/artifacts/**' --no-backrefs --from 'wiki/**' --link-kind frontmatter
```

`--where` keeps the pages whose frontmatter meets a condition, and can be given more than once: a page must meet every one. Only markdown files have frontmatter, so no condition matches any other file, not even `!=` or `!field`. A field is a dotted path into nested mappings, such as `verified.at`, written as a `links.frontmatter` field is in the config: a part ending in `[]` means each element of that list, so `--where 'sources[].id=j-2026-09-16'` keeps the pages where any element of `sources` has that `id`. A part without `[]` does not step into a list, so `sources.id` reaches nothing when `sources` is a list, and `[]` on a value that is not a list reaches nothing either. A field cannot hold `=`, `<`, `>` or `!`, since the first of them starts the operator, and a key holding `.`, `[` or `]` cannot be reached.

| Condition | Keeps the pages where |
|---|---|
| `field=value` | the field equals the value |
| `field!=value` | it does not: `tags!=draft` keeps the pages with no `draft` tag, including those with no `tags` |
| `field<value`, `field<=value`, `field>value`, `field>=value` | the field is below or above the value |
| `field` | the field is there and not null; `as_of:` with no value counts as missing |
| `!field` | the field is missing or null |

A stored number is compared as a number when the value is one too, and anything else as text, character by character, so write dates as ISO 8601 (`2026-04-01`), which sort as text in date order. YAML dates are stored as text, so `as_of: 2026-04-01` is the string `"2026-04-01"`. A list matches when any element does. A range never matches a missing field, a boolean, a null or a mapping. A page whose frontmatter failed to parse meets no condition, not even `!=` or `!field`, since nothing is known of it; `--errors` lists those. `<` and `>` redirect in every shell, so quote each condition: `--where 'as_of<2026-04-01'`.

A value starting with `@` names a field of the same page, in the same path grammar, in place of a literal: `--where 'verified.at<@generated.at'` keeps the pages verified before they were generated. `@@` writes a literal `@`, so `author=@@alice` matches `@alice`, and a value without a leading `@` is always a literal. The two fields compare as numbers when both are numbers, and as text otherwise, and if either is a list, the condition holds when any pair of elements meets it. A missing right-hand field fails `=`, `<`, `<=`, `>` and `>=`, and meets `!=`, which stays the exact negation of `=`.

`--field` shows the frontmatter fields it names, each a dotted path as `--where` takes, and can be given more than once, or with several paths separated by commas: `--field as_of,tracking --field verified.at`. A path naming a list or a mapping shows it whole, a path through `[]` shows the list of the values it reaches, as `--field 'sources[].resource'` does, and one that reaches nothing is left out. A key holding `.`, `,`, `[` or `]` cannot be reached. With `--json`, each file gets a `fields` map keyed by the path as given, leaving out a field the file does not have, giving a field set to YAML null as `null`, and empty for a file whose frontmatter failed to parse. In text, each field ends the file's line as `key=value`, a list or mapping as compact JSON, and with a query on the title line, above the snippet:

```
wiki/x.md  as_of=2026-04-01  tracking=open
```

So a check over a bundle's frontmatter takes one call:

```sh
hippo find --glob 'wiki/**' --where tracking=open --where 'as_of<2026-04-01' --json
hippo find --glob 'wiki/**' --where tracking --where '!as_of' --json
hippo find --glob 'wiki/**' --field as_of,tracking,verified.at --json
```

With a query, `hippo find` looks through every markdown file's title, description, path and body: the body is the text after the frontmatter, as written but with each run of whitespace read as one space and the control characters U+0001 to U+0003, which hippo uses to mark snippets, read as `�`, the title is the frontmatter `title`, or the first level-1 heading when there is none, and the description is the frontmatter `description`, in every workspace, not only in OKF bundles. In the query, the text between a pair of `"` is a phrase, and every other word stands alone; a page matches when it holds every phrase and every word, so `'"integration test" ranking'` needs both the phrase and the word. Every `"` is syntax: an odd number of them is an error, an empty phrase `""` is ignored, and a `"` cannot be searched for. Each word and phrase is matched as text, so punctuation in it is never query syntax: `foo-bar` finds the words `foo` and `bar` side by side, and `*`, `:`, `-`, `AND`, `OR` and `NEAR` mean nothing special. Results rank by BM25, with a match in the title counting for more than one in the description, one in the description for more than one in the path, and one in the path for more than one in the body. Each result shows a snippet of the body around the match, on one line, as written, so the page's own markdown such as `**` shows unchanged. On a terminal the matched words are in bold, unless `NO_COLOR` is set to anything but the empty string; on Windows, hippo turns on the console's ANSI support, and a console that cannot take it shows no bold; piped output and the JSON `snippet` mark nothing. A page that matches only in its title, description or path shows the start of its body. The filters keep only the pages they match, as without a query, and `--limit` counts what is left, 20 unless another is given. Finding nothing is not an error, so `find` exits 0 either way.

The `porter` tokenizer, the default, matches whole words and other forms of them, so `running` finds `runs`. The `trigram` tokenizer matches any run of three or more characters, inside words too, so `dex` finds `index`, but a word shorter than three characters finds nothing. Changing `search.tokenizer` rebuilds the search index on the next command, without reading the files again.

A phrase means what the tokenizer makes of it, and it matches within the title, the path or the body, never across them:

| | `porter` (default) | `trigram` |
|---|---|---|
| A phrase means | The words next to each other and in order, in any of their forms, ignoring punctuation and case | The text as a substring, punctuation included, ignoring case |
| `"integration test"` finds "test integration" | No | No |
| `"(per human, unfiled)"` finds "per humans, unfilled" | Yes | No |

Under `trigram`, a phrase of three or more characters can match although its words are shorter, so `"is a"` finds "this is a test".

The shell removes the outer quotes, so a phrase needs a second layer:

| Shell | Phrase |
|---|---|
| bash, zsh, fish, PowerShell 7.3+ | `hippo find '"integration test"'` |
| Windows PowerShell 5.1 | `hippo find '\"integration test\"'` |
| cmd.exe | `hippo find "\"integration test\""` |

The index lives in `<user cache>/hippo/<hash of workspace root>/index.db`; set `HIPPO_CACHE_DIR` to an absolute path to replace `<user cache>/hippo`. The hash is the SHA-256 of the root's path. On Windows that is the path the OS resolves the folder to, so a different letter case, an 8.3 short name, a junction or a `subst` drive still finds the same index. Each index records its root, which `hippo cache list` checks: `live` when the root still has a `.hippo/config.json`; `orphaned` when it does not but the root or the folder above it exists, as after the workspace was deleted, moved or renamed; `unreachable` when the drive the root was on is not mounted, or when neither the root nor the folder above it exists or they cannot be checked, as for an offline share; and `unknown` when the index records no root or cannot be read. `hippo cache prune` removes the orphaned indexes, and with `--include-unreachable` the unreachable ones too; nothing else removes an index. A moved workspace is indexed afresh at its new path, and its old index stays until pruned.

## Known limits

- **Pathological markdown parses slowly.** A long run of text with no spaces or line breaks that holds many unclosed `[x](` takes time that grows with the square of its length: about 1 s at 50 KB and 14 s at 200 KB. Ordinary pages, even large ones full of links, parse in milliseconds. The cost is paid when the file changes, not on every command, and the answers stay correct. Minified code pasted outside a code block is the realistic way to hit it.
- **A page Markdig cannot parse has no body links.** Blocks nested past Markdig's depth limit make it give up on the page; hippo warns, keeps the page's frontmatter links, and indexes the rest of the workspace.
- **An unreadable page after a link-settings change slows every command.** Changing `bundles` or `links`, or whether a bundle's root `index.md` declares `okf_version`, makes hippo re-read every page to redo its links and findings. If a page cannot be read then, hippo keeps its old links and does not record the new settings as applied, so each later command re-reads every page again, with a warning naming the file, until that page can be read. The answers stay correct; fixing the file's permissions ends it.
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
- A committed script is never edited, even before a release; fixes go forward in a new migration. An index that ran a script never runs it again, so an edit would leave it on the old schema at a version that looks current. `SchemaTests` holds each committed script's checksum and fails when one changes; a new script adds its line. Each index also records a fingerprint of the scripts it ran, and hippo refuses an index whose scripts differ from its own at that version, naming the folder to delete.
- EF cannot model an FTS5 table. The `search` table is created by `migrationBuilder.Sql` in its migration, the model leaves it out, and `SchemaTests` compares only ordinary tables; a change to it is SQL written by hand in a new migration. FTS5 cannot alter a table's columns, so such a migration recreates the table and copies every row across, rowid included, as `AddSearchDescription` does. `SearchIndex.Create`, which recreates the table when the tokenizer changes, must write the same SQL: `SchemaTests` checks that the two agree.

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
