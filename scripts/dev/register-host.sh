#!/usr/bin/env bash
# Registers the Debug build of juvin-host as the com.juvin.host native messaging host for
# native (non-sandboxed) Firefox and Google Chrome on Linux. For development only; the setup
# wizard does this for users.
#
# Usage: scripts/dev/register-host.sh [--unregister]
# Build first: dotnet build src/Juvin.NativeHost
set -euo pipefail

name="com.juvin.host"
firefox_extension_id="extension@juvin.app"
chrome_origins='"chrome-extension://igmlmppjhljfjnepflcpjblbdilkalkg/", "chrome-extension://okfcbcoboinjniofikfnmllkbaeadelo/"'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
host_path="$repo_root/src/Juvin.NativeHost/bin/Debug/net10.0/juvin-host"

firefox_dir="$HOME/.mozilla/native-messaging-hosts"
chrome_dir="${XDG_CONFIG_HOME:-$HOME/.config}/google-chrome/NativeMessagingHosts"

if [[ "${1:-}" == "--unregister" ]]; then
    rm -f "$firefox_dir/$name.json" "$chrome_dir/$name.json"
    echo "Removed $name from Firefox and Chrome."
    exit 0
fi

if [[ $# -gt 0 ]]; then
    echo "Usage: $0 [--unregister]" >&2
    exit 2
fi

if [[ ! -x "$host_path" ]]; then
    echo "juvin-host not found at $host_path. Run: dotnet build src/Juvin.NativeHost" >&2
    exit 1
fi

mkdir -p "$firefox_dir" "$chrome_dir"

cat > "$firefox_dir/$name.json" <<EOF
{
  "name": "$name",
  "description": "Juvin browser connector",
  "path": "$host_path",
  "type": "stdio",
  "allowed_extensions": ["$firefox_extension_id"]
}
EOF

cat > "$chrome_dir/$name.json" <<EOF
{
  "name": "$name",
  "description": "Juvin browser connector",
  "path": "$host_path",
  "type": "stdio",
  "allowed_origins": [$chrome_origins]
}
EOF

echo "Registered $name for Firefox ($firefox_dir) and Chrome ($chrome_dir)."
echo "Host: $host_path"
