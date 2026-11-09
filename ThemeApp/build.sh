#!/usr/bin/env bash
# Builds Prospero Themes on Linux: the app folder (ThemeApp/out/module), or with --release the store
# package (dist/PPSA90210.zip and dist/PPSA90210.json), and with --deploy puts the build on a console.
#
# It brings its own toolchain, so a build does not depend on what happens to be installed:
#   - the .NET SDK pinned below, in a tools folder (not system-wide). The version matters: the
#     NativeAOT runtime pack decides which C library functions the app imports, and with runtime
#     10.0.14 the link fails on functions the console's libc does not have.
#   - PowerShell 7, which runs build.ps1 and package-release.ps1.
#   - the SharpProspero SDK: SHARPPROSPERO_ROOT, --sdk, ../SharpProspero next to this repository, or
#     a clone of the branch below.
#   - libc.prx: third_party/modules/libc.prx, or taken from the latest release ZIP.
#
# On machines with systemd the build runs in a memory-capped scope (PROSPERO_BUILD_MEMORY, 4500M by
# default): NativeAOT compiles take a few GB, and the cap keeps a small machine responsive.
#
# Usage: ThemeApp/build.sh [--release] [--deploy <console-ip>] [--sdk <path>] [--tools <dir>]
set -euo pipefail

DOTNET_SDK_VERSION="10.0.107"     # runtime 10.0.7
PWSH_VERSION="7.6.9"
SDK_REPO="https://github.com/Darkatek7/SharpProspero.git"
SDK_BRANCH="prospero-themes"
APP_REPO="Darkatek7/ProsperoThemes"
TITLE_ID="PPSA90210"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"

release=0
deploy_ip=""
sdk="${SHARPPROSPERO_ROOT:-}"
tools="${PROSPERO_TOOLS:-${XDG_CACHE_HOME:-$HOME/.cache}/prospero-themes-build}"

usage() { sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; }
while [ $# -gt 0 ]; do
    case "$1" in
        --release) release=1 ;;
        --deploy) deploy_ip="${2:?--deploy needs the console IP}"; shift ;;
        --sdk) sdk="${2:?--sdk needs a path}"; shift ;;
        --tools) tools="${2:?--tools needs a path}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

say() { printf '== %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 is needed: $2"; }

need curl "to download the toolchain"
need tar "to unpack the toolchain"
need git "to fetch the SharpProspero SDK"
need python3 "for the packaging and deploy tools"
mkdir -p "$tools"

# --- .NET SDK -------------------------------------------------------------------------------------
dotnet_dir="$tools/dotnet-$DOTNET_SDK_VERSION"
if [ ! -x "$dotnet_dir/dotnet" ] || ! "$dotnet_dir/dotnet" --list-sdks 2>/dev/null | grep -q "^$DOTNET_SDK_VERSION "; then
    say ".NET SDK $DOTNET_SDK_VERSION"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tools/dotnet-install.sh"
    bash "$tools/dotnet-install.sh" --version "$DOTNET_SDK_VERSION" --install-dir "$dotnet_dir" --no-path >/dev/null
fi

# --- PowerShell -----------------------------------------------------------------------------------
pwsh_dir="$tools/pwsh-$PWSH_VERSION"
if [ ! -x "$pwsh_dir/pwsh" ]; then
    say "PowerShell $PWSH_VERSION"
    case "$(uname -m)" in
        x86_64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) die "no PowerShell build for $(uname -m)" ;;
    esac
    mkdir -p "$pwsh_dir"
    curl -fsSL "https://github.com/PowerShell/PowerShell/releases/download/v$PWSH_VERSION/powershell-$PWSH_VERSION-linux-$arch.tar.gz" \
        | tar -xz -C "$pwsh_dir"
    chmod +x "$pwsh_dir/pwsh"
fi

# --- SharpProspero SDK ----------------------------------------------------------------------------
if [ -z "$sdk" ] && [ -d "$repo/../SharpProspero/tools/SharpProspero.Link" ]; then
    sdk="$(cd "$repo/../SharpProspero" && pwd)"
fi
if [ -z "$sdk" ]; then
    sdk="$tools/SharpProspero"
    if [ -d "$sdk/.git" ]; then
        say "SharpProspero ($SDK_BRANCH, updating)"
        git -C "$sdk" fetch -q origin "$SDK_BRANCH"
        git -C "$sdk" checkout -q -B "$SDK_BRANCH" "origin/$SDK_BRANCH"
    else
        say "SharpProspero ($SDK_BRANCH)"
        git clone -q --branch "$SDK_BRANCH" "$SDK_REPO" "$sdk"
    fi
fi
[ -d "$sdk/tools/SharpProspero.Link" ] || die "$sdk is not a SharpProspero checkout"
# Without the region walk in the linker's mprotect the app links fine and crashes as it starts: the
# collector commits across ranges it reserved separately, and only the first one got memory behind it.
grep -q 'JumpIfAtOrAbove("after")' "$sdk/tools/SharpProspero.Link/CompatEmitter.cs" \
    || die "the SDK at $sdk lacks the mprotect region walk; the app would crash at start. Use branch $SDK_BRANCH of $SDK_REPO."

# --- libc.prx -------------------------------------------------------------------------------------
libc="$repo/third_party/modules/libc.prx"
if [ ! -f "$libc" ]; then
    say "libc.prx from the latest release"
    need unzip "to take libc.prx from the release ZIP"
    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' EXIT
    url="$(curl -fsSL "https://api.github.com/repos/$APP_REPO/releases/latest" \
        | python3 -c 'import json,sys; print(next(a["browser_download_url"] for a in json.load(sys.stdin)["assets"] if a["name"].endswith(".zip")))')"
    curl -fsSL "$url" -o "$tmp/release.zip"
    unzip -q -j "$tmp/release.zip" "$TITLE_ID/sce_module/libc.prx" -d "$tmp"
    mkdir -p "$(dirname "$libc")"
    mv "$tmp/libc.prx" "$libc"
fi

# --- Build ----------------------------------------------------------------------------------------
if [ "$release" = 1 ]; then script="$here/package-release.ps1"; args=(); else script="$here/build.ps1"; args=(-Output Folder); fi

env_vars=(
    "PATH=$dotnet_dir:$pwsh_dir:$PATH"
    "DOTNET_ROOT=$dotnet_dir"
    "DOTNET_MULTILEVEL_LOOKUP=0"
    "SHARPPROSPERO_ROOT=$sdk"
    "DOTNET_CLI_TELEMETRY_OPTOUT=1"
    "DOTNET_NOLOGO=1"
    "MSBUILDDISABLENODEREUSE=1"
    "DOTNET_CLI_USE_MSBUILD_SERVER=0"
)
cmd=("$pwsh_dir/pwsh" -NoLogo -NonInteractive -File "$script" "${args[@]}")

say "Build ($(basename "$script"), SDK $sdk)"
cd "$repo"
if command -v systemd-run >/dev/null 2>&1 && systemctl --user show-environment >/dev/null 2>&1; then
    mem="${PROSPERO_BUILD_MEMORY:-4500M}"
    scope_env=()
    for v in "${env_vars[@]}"; do scope_env+=(-E "$v"); done
    systemd-run --user --scope -q -p MemoryMax="$mem" --working-directory="$repo" \
        -E HOME="$HOME" "${scope_env[@]}" "${cmd[@]}"
else
    env "${env_vars[@]}" "${cmd[@]}"
fi

if [ -n "$deploy_ip" ]; then
    say "Deploy to $deploy_ip"
    python3 "$here/deploy-shadowmount.py" --deploy --ip "$deploy_ip"
fi
