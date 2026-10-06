# Building and testing

## Layout

```
src/Hippo/                      the CLI (AOT-compatible; trim and AOT warnings are errors)
src/Hippo.Migrations/           dev-time only: EF Core model for authoring migrations; never shipped
tests/Hippo.Tests.Unit/         one piece of the CLI at a time: parsing, queries, the sweep, migrations
tests/Hippo.Tests.Integration/  whole commands run in-process against a temp workspace
tests/Hippo.Tests.E2E/          hippo run as a separate process, as a user would
tests/Hippo.Tests.Shared/       fixtures the three test projects reference, with no reference to hippo or xunit
docs/dev/                       notes for working on hippo; architecture.md maps the folders inside src/Hippo
```

A test of what a command does belongs in the integration tests, which pass each command its working directory, environment variables and clock. The line between unit and integration is whether a command runs: a unit test calls one class or method directly, and may use temp files and a SQLite file in a temp folder, because the database is a library linked into hippo rather than a service. Parsing alone is a unit test, so the usage errors every command gives, which stop before it runs, are tested there. A command with a `Run` method, as `find` has, may also have unit tests that call it and check the records it returns; the binding, the validators and both forms of output stay in the integration tests. The E2E tests are kept for what only the real process shows: a smoke test per command; a test for each query or JSON shape the smoke test does not reach, so every one runs once as native AOT compiled it; git, which hippo runs with its own environment; and the `--version` flag and the program name in the help, which in-process are the test host's. The E2E project cannot reference hippo's code, which the build enforces.

E2E tests are found by command. Every leaf command in `hippo --help` has a class in `tests/Hippo.Tests.E2E/Commands` named from its words, such as `FindCommandTests` for `find` and `CacheListCommandTests` for `cache list`; the flags of `hippo` itself are tested in `RootCommandTests`. A test that runs several commands belongs to the one it asserts on. Each leaf command's class holds exactly one smoke test, with the trait `Category=Smoke`: a sanity check that the command runs under the published binary. It checks the exit code, that stderr is empty, and that stdout parses as a non-empty JSON array or object, whichever the command writes with `--json`; `init` has no `--json`, so its smoke test checks the first two. A smoke test will often repeat an integration test, and that is expected. `CommandCoverageTests` fails when a leaf command has no class, when its class has no smoke test or more than one, or when a smoke test sits in any other class. Every other test in a command's class, without the Smoke trait, either runs a query path or JSON shape the smoke test does not, such as `find --no-refs` or `backrefs --transitive`, and is named for that path, as `Transitive_backrefs_filtered_by_source_and_link_kind` is, or checks how hippo runs git. The git tests, which are in `FindCommandTests` and check the files git ignores, carry the trait `Topic=Gitignore`. Pick out a group with `--filter`:

```sh
dotnet test tests/Hippo.Tests.E2E -c Release --filter "Category=Smoke"
dotnet test tests/Hippo.Tests.E2E -c Release --filter "Topic=Gitignore"
```

## Build and test

```sh
dotnet build -c Release
dotnet test -c Release
```

Building the E2E tests publishes hippo as native AOT for this machine into `aot/` beside them, and the tests run that binary, as CI does. A build that includes them, the two commands above among them, therefore needs the native AOT toolchain: clang or gcc, or Visual Studio's C++ tools on Windows. The first publish also downloads SQLite (see [Distribution](releasing.md#distribution)). Measured on an Apple silicon Mac, the publish takes about 13 s from cold, about 9 s after a change to hippo and about 1 s when nothing changed. It runs only when the E2E project is built, and testing the integration or unit tests alone never builds it, so that is the inner loop that skips the publish:

```sh
dotnet test tests/Hippo.Tests.Integration -c Release
dotnet test tests/Hippo.Tests.Unit -c Release
```

To run the E2E tests against another binary instead, set `HIPPO_EXE` to its absolute path when building and testing; the build then skips the publish. The quickest is the JIT launcher the build of hippo already writes, `src/Hippo/bin/<Configuration>/net10.0/hippo` (`hippo.exe` on Windows), though it runs hippo under the .NET runtime rather than as native AOT, so it cannot show what only AOT gets wrong. A binary published by hand works too:

```sh
HIPPO_EXE="$PWD/src/Hippo/bin/Release/net10.0/hippo" dotnet test tests/Hippo.Tests.E2E -c Release
dotnet publish src/Hippo -c Release -r osx-arm64 -o artifacts/publish/osx-arm64
HIPPO_EXE="$PWD/artifacts/publish/osx-arm64/hippo" dotnet test tests/Hippo.Tests.E2E -c Release
```
