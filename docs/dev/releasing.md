# Distribution and releasing

## Distribution

Native AOT binaries are built per platform (`osx-arm64`, `osx-x64`, `linux-x64`, `linux-musl-x64`, `win-x64`) with the publish command in [Build and test](building.md#build-and-test). Native AOT cannot cross-compile between operating systems, so a Mac builds only the two macOS binaries; CI builds each binary on its own OS, the musl one in an Alpine container. The `linux-x64` binary's glibc floor, given in [Install](../../README.md#install) and checked by `install.sh` before it downloads, is set by the Ubuntu runner image CI links it on, and CI fails if it rises. On an older glibc, `dotnet tool install` still picks that binary; building it against an older glibc would lower the floor.

CI also archives each binary in the `native` job, once it has passed its tests, on the OS that built it. Each archive holds the binary alone, at its root. The `publish` job in the release workflow writes `SHA256SUMS` over the archives and attaches everything to the GitHub Release in one call. The release's assets are:

```
hippo-<version>-osx-arm64.tar.gz
hippo-<version>-osx-x64.tar.gz
hippo-<version>-linux-x64.tar.gz
hippo-<version>-linux-musl-x64.tar.gz
hippo-<version>-win-x64.zip
SHA256SUMS
```

`SHA256SUMS` has one line per archive in the `sha256sum` format: 64 lowercase hex digits, two spaces, the file name. Each asset is downloaded from `https://github.com/alisterpineda/hippo/releases/download/v<version>/<asset>`. Changing one of these names means changing every place that uses it.

Each binary is a single file with SQLite linked in. The publish downloads the SQLite amalgamation pinned in `src/Hippo/Sqlite.targets` from sqlite.org, checks its SHA-256, compiles it with the C compiler the native AOT link uses (clang or gcc, or Visual Studio's C++ tools on Windows), and leaves out the `e_sqlite3` library the SQLitePCLRaw package ships. Every other build, the tests and the framework-dependent tool package included, still loads that package library, so the two must be the same SQLite version; a unit test fails when they differ. The package decides the version: SQLitePCLRaw.bundle_e_sqlite3, which Microsoft.Data.Sqlite brings in. To move to a new SQLite, move that package, then update the version, URL and SHA-256 in `Sqlite.targets` to match; the version's [release log](https://sqlite.org/changes.html) gives the SHA3-256 of its `sqlite3.c` to check the download against.

hippo also ships as a `dotnet tool`, in one package per native binary plus a framework-dependent package for every other platform, which loads SQLite from the SQLitePCLRaw package. Users install the `hippo` package, which lists the others; the .NET CLI (SDK 10 or later) picks the native one for their platform and falls back to the framework-dependent one only where there is none. Each native package is packed on its own OS, like the binary:

```sh
dotnet pack src/Hippo -c Release -r osx-arm64 -o artifacts/nupkg               # a native package
dotnet pack src/Hippo -c Release -r any -p:PublishAot=false -o artifacts/nupkg  # the framework-dependent package
dotnet pack src/Hippo -c Release -o artifacts/nupkg                             # the package users install
```

Every package carries the same version. When publishing to a feed, push the package users install last, since installing it fails until the one it picks is there. CI uploads them all as the `tool-package` artifact.

To install from the local feed, use `local-feed.nuget.config`. It maps the `hippo` IDs to the local feed, so a local install never pulls the published package:

```sh
dotnet tool install hippo --tool-path artifacts/tool --configfile local-feed.nuget.config \
  --version "$(dotnet msbuild src/Hippo -getProperty:Version)"
artifacts/tool/hippo
```

The feed needs the package users install and the one for this platform. A leftover `bin/Release/net10.0/<rid>/publish/` folder is packed as it is, stale files included, so delete it before packing on a machine that published before.

## Releasing

Changes are listed under `## Unreleased` in `CHANGELOG.md` until they ship. The tag names the version: a tag `v<version>` is built, packed and published as `<version>`, whatever `Version` in `Directory.Build.props` says. That value is only what local builds and CI runs on `main` carry: the next version with `-dev`, such as `0.2.0-dev` once 0.1.0 is out, so a build that is not a release never looks like one. To release, rename `## Unreleased` to `## <version> - <date>` and commit it, tag the commit with `git tag -a v<version> -m "hippo <version>"`, and push the tag with `git push origin v<version>`. Watch the `Release` run it starts with `gh run watch` or in the Actions tab. The workflow runs the full CI, then waits for the reviewer on the `release` environment. Once approved, it pushes the packages to nuget.org with a key from nuget.org trusted publishing, which lasts an hour, in the order the Distribution section gives, the package users install last. It then attaches `hippo-<version>-<rid>.tar.gz` for each platform, `.zip` for `win-x64`, and `SHA256SUMS` to a GitHub Release whose notes are the version's `CHANGELOG.md` section.

Immutable releases are enabled on the repository and must stay on; if the repository is ever recreated, enable them before the first release. Once a release is published, its assets cannot be added, changed or removed, and its tag cannot be moved or deleted; the title, the notes, the prerelease flag and which release is "Latest" can still be edited. A broken release is therefore never patched: it is followed by a new version.

A prerelease, a version with a suffix such as `0.1.0-rc.1`, needs no commit: tag the commit and push the tag. Its notes come from a section for that exact version if there is one, and from `## Unreleased` otherwise. Its GitHub Release is marked as a prerelease, and `dotnet tool install -g hippo` passes it over, so installing it takes `--prerelease` or `--version`. nuget.org never deletes a pushed version, only unlists it, so a broken prerelease is followed by the next one, such as `0.1.0-rc.2`.

If a push fails after some packages went up, rerun the `publish` job; `--skip-duplicate` passes the packages already on nuget.org. `gh release create` makes the release as a draft, uploads the assets, then publishes it, and deletes the draft if an upload or the publish fails, so rerunning the job is enough. Only a cancelled or timed-out job leaves a draft behind: delete it, then rerun. To rehearse a release, run the workflow from the Actions tab with `dry_run` on, optionally with a `version`; without one it uses `Version` in `Directory.Build.props`. It runs everything, the approval included, but prints the files and their order instead of publishing them.
