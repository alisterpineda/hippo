# Install

## Install scripts

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

On Linux arm64 and Windows arm64, which have no native binary yet, the scripts stop and point at the [.NET tool](#net-tool). If another `hippo`, such as the .NET tool, is on your `PATH` ahead of the one installed, they warn that it runs first.

## .NET tool

With the .NET SDK 10 or later, hippo also installs as a .NET tool:

```sh
dotnet tool install -g hippo               # the latest release
dotnet tool install -g hippo --prerelease  # the latest version, prereleases included
```

It installs a native binary on macOS, Linux x64 (glibc or musl) and Windows x64, and elsewhere a build that runs on the .NET runtime. `dotnet tool update -g hippo` upgrades it.

## By hand

Download the archive for your platform from the [GitHub Releases page](https://github.com/alisterpineda/hippo/releases), `hippo-<version>-<rid>.tar.gz`, or `hippo-<version>-win-x64.zip` for Windows, and put the `hippo` binary it holds (`hippo.exe` on Windows) on your `PATH`.

## Platform notes

- **macOS:** the binaries are not notarized, so macOS blocks one downloaded through a browser on its first run. Clear the quarantine flag with `xattr -d com.apple.quarantine hippo`, or download the archive with `curl -LO` instead. Neither `curl` nor the install script sets the flag.
- **Linux x64:** the binary needs glibc 2.38 or newer, which Ubuntu 24.04, Debian 13, Fedora 39 and RHEL 10 have. On older distributions, such as Ubuntu 22.04, Debian 12 and RHEL 8 and 9, it does not start. The install script checks this before it downloads anything.

## Verifying a download

The install scripts check each archive against `SHA256SUMS`. For more than the checksum, GitHub attests every release's assets, and the [GitHub CLI](https://cli.github.com) checks that an archive is the one the release published:

```sh
gh release verify-asset -R alisterpineda/hippo v<version> hippo-<version>-<rid>.tar.gz
```
