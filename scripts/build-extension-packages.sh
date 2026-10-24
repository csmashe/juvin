#!/usr/bin/env bash
# Builds the browser extension packages for the stores: the Chrome Web Store zip, the Firefox Add-ons zip,
# and the source zip addons.mozilla.org asks for because the scripts are bundled.
#
# Usage: scripts/build-extension-packages.sh [output-dir]
#   output-dir  where the packages go (default dist/)
set -euo pipefail

[[ $# -le 1 ]] || { echo "Usage: $0 [output-dir]" >&2; exit 2; }

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
mkdir -p "${1:-$REPO_ROOT/dist}"
OUT=$(cd "${1:-$REPO_ROOT/dist}" && pwd)
EXT="$REPO_ROOT/extension"

command -v zip >/dev/null || { echo "error: zip is required" >&2; exit 1; }

# The source zip is taken from HEAD, so the packages must be built from it too, or a reviewer's rebuild would differ.
if [[ -n $(git -C "$REPO_ROOT" status --porcelain -- extension assets/brand/png LICENSE) ]]; then
    echo "error: extension/, assets/brand/png, or LICENSE has uncommitted changes; commit them first" >&2
    exit 1
fi

cd "$EXT"
npm ci --no-audit --no-fund
npm run build
npm test
VERSION=$(node -p 'require("./src/manifest.json").version')

# The Chrome Web Store gives the store build its own key and ID, so the pinned development key comes out. The app's
# native host must allow that ID before the store build can connect.
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
cp -R dist/chrome "$STAGE/chrome"
node -e '
const fs = require("fs");
const path = process.argv[1];
const manifest = JSON.parse(fs.readFileSync(path, "utf8"));
delete manifest.key;
fs.writeFileSync(path, JSON.stringify(manifest, null, 2) + "\n");
' "$STAGE/chrome/manifest.json"
CHROME_ZIP="$OUT/juvin-chrome-$VERSION.zip"
rm -f "$CHROME_ZIP"
(cd "$STAGE/chrome" && zip -qrX "$CHROME_ZIP" .)

npx web-ext lint --source-dir dist/firefox --self-hosted=false --warnings-as-errors
FIREFOX_ZIP="$OUT/juvin-firefox-$VERSION.zip"
rm -f "$FIREFOX_ZIP"
(cd dist/firefox && zip -qrX "$FIREFOX_ZIP" .)

# What a reviewer needs to rebuild dist/firefox: the extension sources and lock file, and the brand icons build.mjs copies.
SOURCE_ZIP="$OUT/juvin-extension-source-$VERSION.zip"
rm -f "$SOURCE_ZIP"
git -C "$REPO_ROOT" archive --format=zip -o "$SOURCE_ZIP" HEAD -- extension assets/brand/png LICENSE

echo "Built:"
echo "  $CHROME_ZIP"
echo "  $FIREFOX_ZIP"
echo "  $SOURCE_ZIP"
echo "The Chrome store build connects only once the app allows its store ID; see docs/store-listings.md."
