# Architecture

How the code inside `src/Hippo` is laid out and why, for someone reading it for the first time. The README's Layout section lists the projects and the test tiers; this page is about the folders inside the one project that ships.

## The shape in one sentence

A command opens a session, which reads the workspace from disk, brings the SQLite index in line with it, and then answers from the index.

That is a pipeline, and the folders are its stages:

```
disk ──► Workspaces/ ──► Indexing/Sweeper ──► SQLite ──► Indexing/*Queries ──► Commands/
                 ▲                                 ▲
                 └──────────── Okf/ ───────────────┘
```

Every command but `init` and `cache` runs the whole pipeline on each invocation: there is no daemon and no watch mode, so the index is only ever as fresh as the last command made it. The sweep is cheap when nothing changed, which is what makes that acceptable.

## The folders

### `Commands/` — the front end

One static class per command, each with a `Build(CliEnvironment)` that declares its arguments and options with System.CommandLine and sets its action. `Cli.cs` at the top level assembles them into the root command and turns usage errors into exit code 2.

Three shared pieces live here too:

- `WorkspaceSession` is what every workspace command starts from. It finds the workspace, opens and migrates its index, runs the sweep, and hands the command its output writers and the `--json` flag. A command never opens a database itself.
- `Guard` is where every failure ends: a `HippoException` prints its message, anything else prints whole, and both exit 2.
- `Output.cs` holds the `--json` records, the source-generated serializer for them, and `Format`, the text helpers. The JSON records are hippo's contract with scripts, so each is a record of its own and never a row type from `Indexing/`.

A command that has helpers of its own gets a folder, named for the command, with a namespace to match. `Find/` is the one so far and the pattern for the rest:

- `FindCommand.Build` declares the options and validators, as every command does. Its action binds the parse result into a `FindOptions` record, calls `Run`, and prints what comes back.
- `FindCommand.Run` takes the options, the workspace and the connection, and returns the `--json` records. It takes no parse result and writes nothing, so a unit test can ask what `find` would list with no command line and no output to parse. The integration tests keep covering the binding, the validators and the two forms of output.
- `FrontmatterOptions.cs` is the `--where` and `--field` language. It parses conditions and field paths and runs them over a page's stored frontmatter JSON. It is here rather than in `Indexing/` because only `find` speaks it.

A command that fits in one file stays as one file at the root of `Commands/`. The seam is the point, not the folder: a single-file command whose action grows past a screen should get the same `Run` method before it gets a folder.

### `Workspaces/` — the disk side

Everything that reads the workspace on disk and understands what it finds, with no knowledge of SQLite:

- `Workspace` finds the root by walking up to the first `.hippo/config.json`, and `WorkspaceConfig` parses that file.
- `Page` parses one markdown file into its frontmatter, title, description, links and body text. `Frontmatter` is the YAML half of that, `PlainText` the markdown-as-text half, and `DepthLimitedParser` keeps a hostile YAML file from overflowing the stack.
- `GitIgnored` asks git which files it ignores. `Nfd` normalizes Unicode so paths match across filesystems that store them differently.
- `FieldPath` is a dotted path into frontmatter, shared by `find`, the link settings in the config, and page parsing.
- `LintRules` is the catalog of lint rules, workspace rules and OKF rules together, which is what `lint --rule` and `lint --help` read.

### `Indexing/` — the SQLite side

Everything about one workspace's index:

- `IndexDatabase` opens the file and `MigrationRunner` brings its schema up to date, from the SQL scripts embedded under `Migrations/`. The README's Migrations section says how those scripts are authored.
- `Sweeper` is the one writer. It lists the workspace, stats every file, re-hashes only what changed, re-parses only what the hash says changed, and writes the rows in batches through `Batches`. Every write is idempotent, so two processes may sweep at once.
- The `*Queries` classes are the readers: `FileQueries`, `LinkQueries`, `FindingQueries` and `SearchIndex`, each a set of static methods taking a connection and returning records. `IndexMeta` is the key-value table that records whose index this is and what built it.

### `Cache/` — the folder that holds every index

Where indexes live and how `hippo cache` finds them, as opposed to what is inside one:

- `CacheLocation` maps a workspace root to its index path under the user cache folder or `HIPPO_CACHE_DIR`. `CanonicalPath` is the one name of a root that path is keyed by, so every way of reaching a folder finds the same index.
- `CachedIndexes` lists every index in the cache with the state of its workspace, and removes one. `MountPoints` is how it tells an unmounted drive from a deleted workspace.

### `Okf/` — one spec's rules

The Open Knowledge Format checks, kept apart because they are a spec hippo follows rather than something hippo defines. `OkfBundle` says which configured bundles are OKF bundles. `OkfRules` names the rules. `OkfChecks` checks one file alone, so `Page.Parse` runs it and the sweep stores its findings beside the page's links. `IndexSync` is the one rule that compares files to each other, so `lint` works it out from the index on request instead. `IndexEntries` reads the entries of an `index.md`, for page parsing and for `IndexSync`.

### Top-level files

`Program.cs` calls `Cli.Run`. `CliEnvironment` is what a command reads from the process it runs in: the working directory, the environment, the clock, the mount points, and whether output is a terminal. Tests pass their own, which is how whole commands run in-process. `HippoException` is the one exception type a command raises on purpose. `DapperAot.cs` and `Sqlite.targets` are the native AOT plumbing.

## Boundaries worth knowing

**The folders are concerns, not layers.** They depend on each other in a cycle: `Workspaces/` reaches into `Okf/` for the rule names, the spec version, and to run the per-file checks from `Page.Parse`; `Indexing/` reaches into `Okf/` for bundle detection in `Sweeper` and parses pages through `Workspaces/`; and `Okf/` reaches into both `Workspaces/` and `Indexing/`. Inside one assembly this is harmless. It does mean the folders could not be split into separate projects without first deciding whether the OKF checks belong to page parsing or sit above it.

**Two things do not cross into `Indexing/`.** The `--json` records stay in `Commands/`, so a schema change cannot silently change hippo's output. The `--where` and `--field` language of `find` stays in `Commands/` (`FrontmatterOptions`), so a change to it cannot alter what `Indexing/` stores. Only the search text crosses, because `SearchIndex.Query` turns it into an FTS5 match expression.

**`Cache/` sits above `Indexing/`, never below it.** `CachedIndexes` reads an index's meta table to learn its root, but nothing in `Indexing/` knows where indexes live or what else is in the cache. `WorkspaceSession` composes the two: it asks `Cache/` for the path, then hands `Indexing/` the open connection.

## What is deliberately absent

**No dependency-injection container.** Everything is a static class or a record, and the connection, workspace and session are passed as arguments. System.CommandLine 2.0 ships no DI integration, the native AOT build punishes reflection-based containers, and hippo has nothing to swap at runtime. `CliEnvironment` is the one seam tests need.

**No repository interfaces.** The `*Queries` classes are plain static methods over Dapper. An interface in front of them would add a file per table and nothing to test against, since every test runs against a real SQLite file in a temp folder.

**No separate core library.** One project, one binary. The dotnet CLI splits its command definitions from its handlers to keep an AOT-safe tree apart from heavy dependencies; hippo's whole project is AOT-safe, so the split would buy nothing.

## Where a change goes

- A new option on an existing command: that command's file in `Commands/`, and its `*Output` record if the JSON changes, with the matching test in `OutputJsonTests`.
- A new command: a new file in `Commands/`, registered in `Cli.Build`. If it needs the index, it opens a `WorkspaceSession`; if it does not, it runs under `Guard` directly, as `init` and `cache` do. Give it an options record and a `Run` method as `find` has by the rule above, once its action is past a screen, and a folder once it has helpers of its own.
- A new question to ask the index: a method on the fitting `*Queries` class, with a unit test against `TestDatabase`.
- A new fact to store per file: a migration (see the README), a column on the sweep's row records, and the parse in `Workspaces/Page`.
- A new lint rule: its description in `LintRules`, and its name there too, or in `OkfRules` if it is an OKF rule. An OKF rule that judges a file alone goes in `OkfChecks`, which runs only inside OKF bundles. Any other rule is a worked rule in `LintCommand`, reading what the sweep stored, as `frontmatter-syntax` does from `FileQueries.ParseErrors`.
