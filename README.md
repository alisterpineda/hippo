# hippo

A standalone CLI that indexes a markdown notebook into a local SQLite cache and answers structural questions about it: what links where, what is broken, what breaks the notebook's conventions, and where a phrase appears.

Status: phase 0 (skeleton). `hippo` prints `hippo <version>: nothing indexed yet`.

## Layout

```
src/Hippo/             the CLI (AOT-compatible; trim and AOT warnings are errors)
src/Hippo.Migrations/  dev-time only: EF Core model for authoring migrations; never shipped
tests/Hippo.Tests/     references both source projects
```

## Build and test

```sh
dotnet build -c Release
dotnet test -c Release
```

To run the process tests against a published binary instead of the build output, set `HIPPO_EXE` to its absolute path:

```sh
dotnet publish src/Hippo -c Release -r osx-arm64 -p:PublishAot=true -o artifacts/publish/osx-arm64
HIPPO_EXE="$PWD/artifacts/publish/osx-arm64/hippo" dotnet test -c Release
```

## Distribution

Native AOT binaries are built per platform (`osx-arm64`, `osx-x64`, `linux-x64`, `win-x64`) with the publish command above. Native AOT cannot cross-compile between operating systems, so a Mac builds only the two macOS binaries; CI builds each binary on its own OS.

The `dotnet tool` package installs from a local feed. `local-feed.nuget.config` limits the install to that feed, since the `hippo` ID is not reserved on nuget.org:

```sh
dotnet pack src/Hippo -c Release -o artifacts/nupkg
dotnet tool install hippo --tool-path artifacts/tool --configfile local-feed.nuget.config \
  --version "$(dotnet msbuild src/Hippo -getProperty:Version)"
artifacts/tool/hippo
```
