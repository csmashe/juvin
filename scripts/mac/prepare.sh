#!/usr/bin/env bash
# Prepares a fresh Mac for the macOS checklist: Google Chrome and Firefox, the .NET SDK from
# global.json, Node for the extension, the extension build, the dmg built and installed to
# /Applications, kit host manifests for both browsers, and a check that the model server answers at
# 127.0.0.1:11434. Every step is skipped when its result is already there, so a rerun is cheap.
# Runs on the Mac with its own /bin/bash 3.2, from the repository root or anywhere inside it.
#
# Usage: scripts/mac/prepare.sh [--ci]
#   --ci   the GitHub runner: the model server is the local stub, so a missing one is an error
# Environment:
#   JUVIN_KIT_CACHE    where the .NET SDK, NuGet packages, and dotnet-install.sh are kept
#                      (default ~/drive/.cache/juvin-kit when ~/drive exists, else ~/.cache/juvin-kit)
#   JUVIN_KIT_FORCE_DOTNET  set to install the SDK into the cache even when a suitable dotnet is on PATH
#   JUVIN_KIT_VERSION  dmg version (default the <Version> in Directory.Build.props)
#   JUVIN_SENTRY_DSN   passed through to the dmg build
set -euo pipefail

CI_MODE=false
case ${1:-} in
    --ci) CI_MODE=true ;;
    "") ;;
    *)
        echo "Usage: $0 [--ci]" >&2
        exit 2
        ;;
esac

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$REPO_ROOT"
# shellcheck source=scripts/mac/kit-env.sh
. "$REPO_ROOT/scripts/mac/kit-env.sh"

[[ $(uname -s) == Darwin ]] || {
    echo "error: this script runs on macOS" >&2
    exit 1
}

STEP_NAMES=()
STEP_RESULTS=()
STEP_SECONDS=()

summary() {
    local i
    echo
    echo "Prepare summary"
    printf '  %-28s %-8s %s\n' Step Result Seconds
    for ((i = 0; i < ${#STEP_NAMES[@]}; i++)); do
        printf '  %-28s %-8s %s\n' "${STEP_NAMES[$i]}" "${STEP_RESULTS[$i]}" "${STEP_SECONDS[$i]}"
    done
}
trap summary EXIT

# Runs one step in a subshell, so errexit holds inside it, and records its result and time. A step
# returns 3 when it had nothing to do and 4 for a warning; any other failure stops the script.
step() {
    local name=$1 started=$SECONDS status
    shift
    echo
    echo "==> $name"
    set +e
    (
        set -e
        "$@"
    )
    status=$?
    set -e
    # A step may have installed a tool that changes PATH.
    # shellcheck source=scripts/mac/kit-env.sh
    . "$REPO_ROOT/scripts/mac/kit-env.sh"
    STEP_NAMES+=("$name")
    STEP_SECONDS+=("$((SECONDS - started))")
    case $status in
        0) STEP_RESULTS+=(ok) ;;
        3) STEP_RESULTS+=(skipped) ;;
        4) STEP_RESULTS+=(warning) ;;
        *)
            STEP_RESULTS+=(FAILED)
            echo "error: step '$name' failed with status $status" >&2
            exit 1
            ;;
    esac
}

install_cask() {
    local app=$1 cask=$2
    if [[ -d "/Applications/$app" ]]; then
        echo "    /Applications/$app is there"
        return 3
    fi
    brew install --cask "$cask"
    [[ -d "/Applications/$app" ]]
}

install_dotnet() {
    local wanted
    wanted=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' global.json | head -n1)
    if [[ -z ${JUVIN_KIT_FORCE_DOTNET:-} ]] && dotnet --version >/dev/null 2>&1; then
        echo "    dotnet $(dotnet --version) satisfies global.json ($wanted)"
        return 3
    fi
    mkdir -p "$KIT_CACHE"
    if [[ ! -s $KIT_CACHE/dotnet-install.sh ]]; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$KIT_CACHE/dotnet-install.sh"
    fi
    # global.json rolls forward to the latest feature band, so the newest SDK of the major.minor channel satisfies it.
    if [[ -x $KIT_DOTNET_DIR/dotnet ]] && "$KIT_DOTNET_DIR/dotnet" --version >/dev/null 2>&1; then
        echo "    the kit's dotnet $("$KIT_DOTNET_DIR/dotnet" --version) is installed"
        return 3
    fi
    /bin/bash "$KIT_CACHE/dotnet-install.sh" --channel "${wanted%.*}" --install-dir "$KIT_DOTNET_DIR" --no-path
    "$KIT_DOTNET_DIR/dotnet" --version
}

install_node() {
    local major
    major=$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || echo 0)
    if ((major >= KIT_NODE_MAJOR)); then
        echo "    node $(node --version) meets the extension's engines (>=$KIT_NODE_MAJOR)"
        return 3
    fi
    if [[ -s ${NVM_DIR:-$HOME/.nvm}/nvm.sh ]]; then
        # shellcheck disable=SC1091
        . "${NVM_DIR:-$HOME/.nvm}/nvm.sh"
        nvm install "$KIT_NODE_MAJOR"
        nvm alias default "$KIT_NODE_MAJOR"
    else
        brew install "node@$KIT_NODE_MAJOR"
        brew link --overwrite --force "node@$KIT_NODE_MAJOR"
    fi
}

build_extension() {
    local stamp=$KIT_STATE/extension.stamp current
    current=$(find extension/src extension/package-lock.json extension/build.mjs assets/brand/png -type f -print0 \
        | xargs -0 shasum | shasum | cut -c1-40)
    if [[ -f $stamp && $(cat "$stamp") == "$current" && -f extension/dist/chrome/manifest.json && -f extension/dist/firefox/manifest.json ]]; then
        echo "    extension/dist is up to date"
        return 3
    fi
    (cd extension && npm ci --no-audit --no-fund && npm run build)
    echo "$current" >"$stamp"
}

build_dmg() {
    local dmg=dist/Juvin-$KIT_VERSION-arm64.dmg stamp=$KIT_STATE/dmg.stamp current
    current=$(find src scripts/build-mac-dmg.sh scripts/mac/entitlements.plist Directory.Build.props Directory.Packages.props \
        -type f ! -path '*/bin/*' ! -path '*/obj/*' -print0 | xargs -0 shasum | shasum | cut -c1-40)
    if [[ -s $dmg && -f $stamp && $(cat "$stamp") == "$current" ]]; then
        echo "    $dmg is up to date"
        return 3
    fi
    /bin/bash scripts/build-mac-dmg.sh "$KIT_VERSION" dist
    echo "$current" >"$stamp"
}

install_app() {
    local dmg=dist/Juvin-$KIT_VERSION-arm64.dmg mount
    if [[ -d /Applications/Juvin.app && -f $KIT_STATE/installed.stamp && $(cat "$KIT_STATE/installed.stamp") == "$(shasum "$dmg" | cut -c1-40)" ]]; then
        echo "    /Applications/Juvin.app is this dmg's build"
        return 3
    fi
    if pgrep -x juvin >/dev/null; then
        echo "    quitting the running Juvin to replace it"
        pkill -TERM -x juvin || true
        for _ in $(seq 20); do
            pgrep -x juvin >/dev/null || break
            sleep 0.5
        done
        pkill -KILL -x juvin 2>/dev/null || true
    fi
    mount=$(mktemp -d "${TMPDIR:-/tmp}/juvin-kit-mnt.XXXXXX")
    hdiutil attach "$dmg" -readonly -nobrowse -noautoopen -mountpoint "$mount" >/dev/null
    rm -rf /Applications/Juvin.app
    ditto "$mount/Juvin.app" /Applications/Juvin.app
    hdiutil detach "$mount" >/dev/null
    rmdir "$mount"
    "$LSREGISTER" -f /Applications/Juvin.app >/dev/null 2>&1 || true
    shasum "$dmg" | cut -c1-40 >"$KIT_STATE/installed.stamp"
    ls /Applications/Juvin.app/Contents/MacOS/juvin /Applications/Juvin.app/Contents/MacOS/juvin-host
}

# The kit's manifests carry their own description, so the wizard reads them as Needs update and M-10
# can tell them from the ones Register writes. A manifest the app wrote is left alone.
write_manifests() {
    local dir family file wrote=false
    for dir in "$CHROME_HOSTS_DIR" "$FIREFOX_HOSTS_DIR"; do
        file=$dir/com.juvin.host.json
        if [[ -f $file ]] && ! grep -q "$KIT_MANIFEST_DESCRIPTION" "$file"; then
            echo "    $file was written by the app; left alone"
            continue
        fi
        family=chrome
        if [[ $dir == "$FIREFOX_HOSTS_DIR" ]]; then
            family=firefox
        fi
        mkdir -p "$dir"
        kit_manifest "$family" >"$file"
        echo "    wrote $file"
        wrote=true
    done
    $wrote || return 3
}

check_model_server() {
    if curl -fsS -m 5 http://127.0.0.1:11434/v1/models >"$KIT_STATE/models.json" 2>/dev/null; then
        echo "    127.0.0.1:11434 answers with $(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1])).get("data", [])))' "$KIT_STATE/models.json") models"
        return 0
    fi
    if $CI_MODE; then
        echo "error: the model stub does not answer at 127.0.0.1:11434" >&2
        return 1
    fi
    echo "warning: nothing answers at 127.0.0.1:11434. Start the tunnel from the Linux machine: scripts/mac/session.sh tunnel" >&2
    return 4
}

mkdir -p "$KIT_STATE"
step "Google Chrome" install_cask "Google Chrome.app" google-chrome
step "Firefox" install_cask "Firefox.app" firefox
step ".NET SDK" install_dotnet
step "Node" install_node
step "Extension build" build_extension
step "dmg build" build_dmg
step "Install Juvin.app" install_app
step "Kit host manifests" write_manifests
step "Model server" check_model_server
