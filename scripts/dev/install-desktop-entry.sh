#!/usr/bin/env bash
# Installs a desktop entry and icons for the Debug build, so GNOME shows the Juvin icon in the
# dock. GNOME takes dock icons only from an installed entry whose StartupWMClass matches the
# window. For development only; the deb and rpm install their own entry and the AppImage writes
# one on first run.
#
# Usage: scripts/dev/install-desktop-entry.sh [--uninstall]
# Build first: dotnet build src/Juvin.App
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
app_path="$repo_root/src/Juvin.App/bin/Debug/net10.0/juvin"

data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
entry="$data_home/applications/juvin-dev.desktop"
icons="$data_home/icons/hicolor"
icon_sizes=(16 22 24 32 48 128 256 512)

if [[ "${1:-}" == "--uninstall" ]]; then
    rm -f "$entry"
    echo "Removed $entry."
    exit 0
fi

if [[ $# -gt 0 ]]; then
    echo "Usage: $0 [--uninstall]" >&2
    exit 2
fi

if [[ ! -x "$app_path" ]]; then
    echo "juvin not found at $app_path. Run: dotnet build src/Juvin.App" >&2
    exit 1
fi

for size in "${icon_sizes[@]}"; do
    mkdir -p "$icons/${size}x${size}/apps"
    cp "$repo_root/assets/brand/png/juvin-mark-color-$size.png" "$icons/${size}x${size}/apps/juvin.png"
done
mkdir -p "$icons/symbolic/apps"
cp "$repo_root/assets/brand/svg/juvin-symbolic.svg" "$icons/symbolic/apps/juvin-symbolic.svg"
# GTK rescans a theme only when its root directory's time changes.
touch "$icons"

# Exec quoting per the Desktop Entry spec: \ " ` $ take a backslash inside quotes, then every backslash doubles.
exec_path=$(printf '%s' "$app_path" | sed -e 's/[\\"`$]/\\&/g' -e 's/\\/\\\\/g' -e 's/%/%%/g')

mkdir -p "$(dirname "$entry")"
cat >"$entry" <<EOF
[Desktop Entry]
Type=Application
Name=Juvin (dev)
Comment=Works beside you
Exec="$exec_path"
Icon=juvin
Terminal=false
Categories=Office;
StartupNotify=false
StartupWMClass=juvin
EOF

echo "Installed $entry."
echo "App: $app_path"
