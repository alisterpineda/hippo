#!/bin/sh
# Installs hippo's native binary from a GitHub release, after checking it against the release's SHA256SUMS:
#
#   curl -fsSL https://raw.githubusercontent.com/alisterpineda/hippo/main/install.sh | sh
#
# HIPPO_VERSION      the version to install, such as 0.1.0 or a prerelease such as 0.1.0-rc.1; the latest release
#                    otherwise
# HIPPO_INSTALL_DIR  where hippo goes; ~/.local/bin otherwise. The script never uses sudo.
# HIPPO_DOWNLOAD_BASE  replaces https://github.com/alisterpineda/hippo/releases/download; CI points it at a local
#                    server, which may be plain HTTP
#
# The asset names and the SHA256SUMS format are the release's asset contract, in docs/dev/releasing.md. Everything
# runs from main, called on the last line, so a download cut short runs nothing.

set -eu

repo=https://github.com/alisterpineda/hippo
tmp=
stage=

die() {
    echo "hippo-install: $*" >&2
    exit 1
}

cleanup() {
    if [ -n "$tmp" ]; then rm -rf "$tmp"; fi
    if [ -n "$stage" ]; then rm -rf "$stage"; fi
}

# GitHub needs a User-Agent. Its URLs are HTTPS only; a HIPPO_DOWNLOAD_BASE set by the user is taken as given.
github_curl() {
    curl -A hippo-install --proto '=https' --tlsv1.2 "$@"
}

download_curl() {
    if [ -n "${HIPPO_DOWNLOAD_BASE:-}" ]; then
        curl -A hippo-install "$@"
    else
        github_curl "$@"
    fi
}

# Prints the HTTP status, so a missing asset can be told from a failed connection.
download() {
    download_curl -sSL -o "$2" -w '%{http_code}' "$1" || die "could not download $1"
}

main() {
    for cmd in curl tar; do
        command -v "$cmd" >/dev/null 2>&1 || die "$cmd is needed but was not found"
    done
    if command -v sha256sum >/dev/null 2>&1; then
        sha256=sha256sum
    elif command -v shasum >/dev/null 2>&1; then
        sha256='shasum -a 256'
    else
        die "sha256sum or shasum is needed but neither was found"
    fi

    os=$(uname -s)
    arch=$(uname -m)
    case "$os $arch" in
        'Darwin arm64')
            rid=osx-arm64
            ;;
        'Darwin x86_64')
            # A shell under Rosetta reports x86_64 on Apple silicon, where the arm64 binary is the one to run.
            if [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || true)" = 1 ]; then
                rid=osx-arm64
            else
                rid=osx-x64
            fi
            ;;
        'Linux x86_64')
            # The glibc binary does not run on musl, so Alpine and its like get their own.
            if [ -e /lib/ld-musl-x86_64.so.1 ] || ldd --version 2>&1 | grep -q musl; then
                rid=linux-musl-x64
            else
                rid=linux-x64
            fi
            ;;
        'Linux aarch64' | 'Linux arm64')
            die "hippo has no Linux arm64 binary yet; install it as a .NET tool: dotnet tool install -g hippo"
            ;;
        *)
            die "hippo has no binary for $os $arch; see Install in the README for other ways to get it: $repo#install"
            ;;
    esac

    # The linux-x64 binary links against the glibc of the image CI builds it on, and the README gives the same floor.
    # Without getconf, the binary's own error on its first run is the fallback.
    glibc_floor=2.38
    if [ "$rid" = linux-x64 ] && libc=$(getconf GNU_LIBC_VERSION 2>/dev/null); then
        glibc=${libc#glibc }
        oldest=$(printf '%s\n' "$glibc_floor" "$glibc" | sort -V 2>/dev/null | head -n 1)
        if [ "$oldest" = "$glibc" ] && [ "$glibc" != "$glibc_floor" ]; then
            die "hippo needs glibc $glibc_floor or newer, which Ubuntu 24.04, Debian 13, Fedora 39 and RHEL 10 have; this system has glibc $glibc"
        fi
    fi

    # The latest release is read from the redirect GitHub answers releases/latest with, which ends in its tag. Unlike
    # the REST API, it is not metered, and it skips prereleases.
    if [ -n "${HIPPO_VERSION:-}" ]; then
        version=${HIPPO_VERSION#v}
    else
        latest=$(github_curl -fsSI -o /dev/null -w '%{redirect_url}' "$repo/releases/latest") || latest=
        case $latest in
            */tag/v*) version=${latest##*/tag/v} ;;
            *) version= ;;
        esac
        [ -n "$version" ] || die "could not find the latest release; set HIPPO_VERSION"
    fi

    asset=hippo-$version-$rid.tar.gz
    base=${HIPPO_DOWNLOAD_BASE:-$repo/releases/download}/v$version

    trap cleanup EXIT
    trap 'exit 1' INT TERM
    tmp=$(mktemp -d)

    echo "Downloading $asset"
    status=$(download "$base/$asset" "$tmp/$asset")
    case $status in
        200) ;;
        404) die "release v$version has no $asset; check HIPPO_VERSION, or that v$version is a release" ;;
        *) die "could not download $base/$asset: HTTP $status" ;;
    esac
    status=$(download "$base/SHA256SUMS" "$tmp/SHA256SUMS")
    [ "$status" = 200 ] || die "could not download $base/SHA256SUMS: HTTP $status"

    expected=$(awk -v name="$asset" '$2 == name { print $1; exit }' "$tmp/SHA256SUMS")
    [ -n "$expected" ] || die "SHA256SUMS in release v$version has no line for $asset; nothing was installed"
    actual=$($sha256 "$tmp/$asset")
    actual=${actual%% *}
    [ "$actual" = "$expected" ] ||
        die "$asset did not match the published checksum in SHA256SUMS; nothing was installed"

    dir=${HIPPO_INSTALL_DIR:-$HOME/.local/bin}
    mkdir -p "$dir" 2>/dev/null && [ -w "$dir" ] ||
        die "cannot write to $dir; set HIPPO_INSTALL_DIR to a directory you can write to"
    # Absolute, for the PATH checks and the line printed below.
    dir=$(cd "$dir" && pwd)

    # The binary is unpacked beside where it goes, so the mv into place is a rename on one filesystem rather than a
    # copy that a failure could leave half written.
    # -o leaves the files owned by whoever runs the script: as root, tar would otherwise give them the owner stored in
    # the archive, the uid of the CI runner that made it.
    stage=$dir/.hippo-install.$$
    mkdir "$stage"
    tar -xzof "$tmp/$asset" -C "$stage"
    [ -f "$stage/hippo" ] || die "$asset holds no hippo binary; nothing was installed"
    chmod +x "$stage/hippo"
    # Run before it replaces anything, so a binary that cannot start here leaves the installed one in place.
    installed=$("$stage/hippo" --version) || die "the downloaded hippo did not run on this system; nothing was installed"
    mv -f "$stage/hippo" "$dir/hippo"
    echo "Installed hippo $installed to $dir/hippo"

    case ":$PATH:" in
        *":$dir:"*) ;;
        *)
            export_line="  export PATH=\"$dir:\$PATH\""
            case ${SHELL:-} in
                */fish)
                    echo "$dir is not on your PATH. Add it once, then open a new terminal:"
                    echo "  fish_add_path \"$dir\""
                    ;;
                */zsh)
                    echo "$dir is not on your PATH. Add this line to ~/.zshrc, then open a new terminal:"
                    echo "$export_line"
                    ;;
                */bash)
                    # macOS terminals start bash as a login shell, which reads ~/.bash_profile and not ~/.bashrc.
                    rc=.bashrc
                    if [ "$os" = Darwin ]; then rc=.bash_profile; fi
                    echo "$dir is not on your PATH. Add this line to ~/$rc, then open a new terminal:"
                    echo "$export_line"
                    ;;
                *)
                    echo "$dir is not on your PATH. Add this line to your shell's startup file, then open a new terminal:"
                    echo "$export_line"
                    ;;
            esac
            ;;
    esac

    # A hippo installed as a .NET tool, in ~/.dotnet/tools, is the likely one.
    found=$(command -v hippo 2>/dev/null || true)
    if [ -n "$found" ] && [ "$found" != "$dir/hippo" ]; then
        echo "Warning: another hippo is on your PATH at $found and runs first. If it is the .NET tool, remove it with: dotnet tool uninstall -g hippo" >&2
    fi

    echo "Rerun this script to update."
}

main "$@"
