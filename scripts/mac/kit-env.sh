# shellcheck shell=bash disable=SC2034
# Shared settings for the macOS session kit, sourced by prepare.sh and run-automated.sh on the Mac.
# Sets paths, puts the kit's .NET SDK and Node on PATH when they are installed, and defines the
# manifest the kit writes. Safe to source more than once.

KIT_REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
KIT_STATE=${JUVIN_KIT_STATE:-$HOME/.juvin-kit}
if [[ -n ${JUVIN_KIT_CACHE:-} ]]; then
    KIT_CACHE=$JUVIN_KIT_CACHE
elif [[ -d $HOME/drive ]]; then
    # NoMac keeps ~/drive between sessions, so the SDK and packages are downloaded once.
    KIT_CACHE=$HOME/drive/.cache/juvin-kit
else
    KIT_CACHE=$HOME/.cache/juvin-kit
fi
KIT_DOTNET_DIR=$KIT_CACHE/dotnet
if [[ -x $KIT_DOTNET_DIR/dotnet && :$PATH: != *":$KIT_DOTNET_DIR:"* ]]; then
    export DOTNET_ROOT=$KIT_DOTNET_DIR
    export PATH=$KIT_DOTNET_DIR:$PATH
fi
export NUGET_PACKAGES=${NUGET_PACKAGES:-$KIT_CACHE/nuget}
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

KIT_NODE_MAJOR=$(sed -n 's/.*"node": *">=\([0-9]*\).*/\1/p' "$KIT_REPO_ROOT/extension/package.json" | head -n1)
KIT_NODE_MAJOR=${KIT_NODE_MAJOR:-24}
if [[ -s ${NVM_DIR:-$HOME/.nvm}/nvm.sh ]] \
    && (($(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || echo 0) < KIT_NODE_MAJOR)); then
    # shellcheck disable=SC1091
    . "${NVM_DIR:-$HOME/.nvm}/nvm.sh"
    nvm use --silent "$KIT_NODE_MAJOR" >/dev/null 2>&1 || true
fi

KIT_VERSION=${JUVIN_KIT_VERSION:-$(sed -n 's/.*<Version[^>]*>\(.*\)<\/Version>.*/\1/p' "$KIT_REPO_ROOT/Directory.Build.props" | head -n1)}

LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
APP=/Applications/Juvin.app
APP_EXE=$APP/Contents/MacOS/juvin
HOST_EXE=$APP/Contents/MacOS/juvin-host
CLI_EXE=$APP/Contents/MacOS/juvin-cli
DATA_DIR="$HOME/Library/Application Support/Juvin"
CHROME_HOSTS_DIR="$HOME/Library/Application Support/Google/Chrome/NativeMessagingHosts"
FIREFOX_HOSTS_DIR="$HOME/Library/Application Support/Mozilla/NativeMessagingHosts"
CHROME_ORIGINS='"chrome-extension://igmlmppjhljfjnepflcpjblbdilkalkg/", "chrome-extension://okfcbcoboinjniofikfnmllkbaeadelo/"'
FIREFOX_EXTENSION_ID=extension@juvin.app
APP_DESCRIPTION="Juvin browser connector"
KIT_MANIFEST_DESCRIPTION="Juvin browser connector (session kit)"

# Prints the kit's host manifest for chrome or firefox: the app's shape with the kit's description.
kit_manifest() {
    local allow
    if [[ $1 == firefox ]]; then
        allow="\"allowed_extensions\": [\"$FIREFOX_EXTENSION_ID\"]"
    else
        allow="\"allowed_origins\": [$CHROME_ORIGINS]"
    fi
    cat <<EOF
{
  "name": "com.juvin.host",
  "description": "$KIT_MANIFEST_DESCRIPTION",
  "path": "$HOST_EXE",
  "type": "stdio",
  $allow
}
EOF
}
