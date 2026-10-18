#!/usr/bin/env bash
# Checks the dmg from build-mac-dmg.sh on this Mac: the image and its layout, the bundle's Info.plist and
# signatures, a copy into a scratch Applications folder, a run of juvin-host and juvin-cli, and a start of
# the app through Launch Services until its control socket answers --trigger. Gatekeeper's verdict is
# printed but not enforced, since an ad-hoc signature is always rejected.
#
# Usage: scripts/smoke-test-mac-dmg.sh <version> [package-dir]
# JUVIN_MAC_ARCH picks the dmg as it does for the build (arm64 by default). Juvin must not be running. The app
# uses ~/Library/Application Support/Juvin (Launch Services does not pass a changed HOME on); when that folder
# did not exist before the test it is removed afterwards.
set -euo pipefail

usage() {
    echo "Usage: $0 <version> [package-dir]" >&2
    exit 2
}

[[ $# -ge 1 && $# -le 2 ]] || usage
VERSION=$1
NUMERIC_VERSION=${VERSION%%-*}
ARCH=${JUVIN_MAC_ARCH:-arm64}
REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PKG_DIR=$(cd "${2:-$REPO_ROOT/dist}" && pwd)

DMG=$PKG_DIR/Juvin-$VERSION-$ARCH.dmg
BUNDLE_ID=com.juvin.app
HOST_MESSAGE="could not connect to Juvin"
ENTITLEMENT=com.apple.security.cs.allow-jit
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

fail() {
    echo "FAIL: $*" >&2
    exit 1
}
pass() { echo "ok: $*"; }

[[ $(uname -s) == Darwin ]] || fail "this script runs on macOS"

# Resolved, because /tmp is a link and the process list shows the real path.
WORK=$(cd "$(mktemp -d "${TMPDIR:-/tmp}/juvin-smoke.XXXXXX")" && pwd -P)
MOUNT=$WORK/mnt
APPLICATIONS=$WORK/Applications
APP=$APPLICATIONS/Juvin.app
EXE=$APP/Contents/MacOS/juvin
DATA_DIR="$HOME/Library/Application Support/Juvin"
SOCKET=$DATA_DIR/Runtime/control.sock
APP_LOG=$WORK/juvin.log
DATA_DIR_EXISTED=false
if [[ -e $DATA_DIR ]]; then
    DATA_DIR_EXISTED=true
fi
# With Launch at login on, every start points the LaunchAgent at the running executable, here the scratch copy.
LAUNCH_AGENT=$HOME/Library/LaunchAgents/com.juvin.app.plist
LAUNCH_AGENT_SAVED=$WORK/launch-agent.plist
if [[ -e $LAUNCH_AGENT ]]; then
    cp -p "$LAUNCH_AGENT" "$LAUNCH_AGENT_SAVED"
fi
PID=

app_pid() { pgrep -f "^$EXE" | head -n1 || true; }

cleanup() {
    local pid
    pid=$(app_pid)
    if [[ -n $pid ]]; then
        kill -KILL "$pid" 2>/dev/null || true
    fi
    if [[ -d $MOUNT ]]; then
        hdiutil detach "$MOUNT" -force >/dev/null 2>&1 || true
    fi
    if [[ -d $APP ]]; then
        "$LSREGISTER" -u "$APP" >/dev/null 2>&1 || true
    fi
    if ! $DATA_DIR_EXISTED; then
        rm -rf "$DATA_DIR"
    fi
    if [[ -e $LAUNCH_AGENT_SAVED ]]; then
        cp -p "$LAUNCH_AGENT_SAVED" "$LAUNCH_AGENT"
    else
        rm -f "$LAUNCH_AGENT"
    fi
    rm -rf "$WORK"
}
trap cleanup EXIT

show_log() {
    if [[ -f $APP_LOG ]]; then
        sed 's/^/  juvin log: /' "$APP_LOG" >&2
    fi
}

plist_value() { /usr/libexec/PlistBuddy -c "Print :$1" "$APP/Contents/Info.plist" 2>/dev/null || true; }

expect_plist() {
    local actual
    actual=$(plist_value "$1")
    [[ $actual == "$2" ]] || fail "Info.plist $1 is '$actual', expected '$2'"
}

# Checks that a file carries a valid signature with the hardened runtime and, for executables, the JIT entitlement
# and no DYLD environment entitlement.
expect_signed() {
    local details
    codesign --verify --strict "$1" 2>"$WORK/codesign.err" || {
        cat "$WORK/codesign.err" >&2
        fail "$1 does not verify"
    }
    details=$(codesign --display --verbose=2 "$1" 2>&1)
    [[ $details == *"(runtime)"* || $details == *",runtime"* || $details == *"runtime,"* ]] \
        || fail "$1 is not signed with the hardened runtime"
    if [[ ${2:-} == executable ]]; then
        details=$(codesign --display --entitlements - --xml "$1" 2>/dev/null)
        [[ $details == *"$ENTITLEMENT"* ]] || fail "$1 lacks the $ENTITLEMENT entitlement"
        [[ $details != *allow-dyld-environment-variables* ]] || fail "$1 allows DYLD environment variables"
    fi
}

[[ -s $DMG ]] || fail "$DMG is missing or empty"
[[ -z $(pgrep -x juvin || true) ]] || fail "a Juvin is already running; quit it first"
hdiutil verify "$DMG" >/dev/null 2>&1 || fail "hdiutil verify rejected $DMG"
pass "the dmg exists and verifies"

mkdir -p "$MOUNT"
hdiutil attach "$DMG" -readonly -nobrowse -noautoopen -mountpoint "$MOUNT" >/dev/null || fail "could not mount $DMG"
[[ -d $MOUNT/Juvin.app ]] || fail "the dmg lacks Juvin.app"
[[ -L $MOUNT/Applications && $(readlink "$MOUNT/Applications") == /Applications ]] \
    || fail "the dmg lacks the Applications link"
mkdir -p "$APPLICATIONS"
ditto "$MOUNT/Juvin.app" "$APP"
hdiutil detach "$MOUNT" >/dev/null || fail "could not detach $DMG"
rmdir "$MOUNT"
pass "the dmg holds Juvin.app and the Applications link; copied to $APPLICATIONS"

plutil -lint "$APP/Contents/Info.plist" >/dev/null || fail "Info.plist does not lint"
expect_plist CFBundleIdentifier "$BUNDLE_ID"
expect_plist CFBundleExecutable juvin
expect_plist CFBundleName Juvin
expect_plist CFBundlePackageType APPL
expect_plist CFBundleShortVersionString "$NUMERIC_VERSION"
expect_plist CFBundleVersion "$NUMERIC_VERSION"
expect_plist CFBundleIconFile juvin.icns
expect_plist NSHighResolutionCapable true
[[ $(plist_value LSMinimumSystemVersion) =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]] || fail "LSMinimumSystemVersion is not a version"
[[ -n $(plist_value NSLocalNetworkUsageDescription) ]] || fail "Info.plist lacks NSLocalNetworkUsageDescription"
[[ -z $(plist_value LSUIElement) ]] || fail "Info.plist sets LSUIElement"
[[ -s $APP/Contents/Resources/juvin.icns ]] || fail "the bundle lacks its icon"
[[ -s $APP/Contents/Resources/LICENSE ]] || fail "the bundle lacks the LICENSE"
for path in juvin juvin-host juvin-cli libAvaloniaNative.dylib Assets/juvin-mark-color-32.png Resources/Localization/en.json; do
    [[ -e $APP/Contents/MacOS/$path ]] || fail "the bundle lacks Contents/MacOS/$path"
done
[[ ! -e $APP/Contents/MacOS/juvin-host.dll ]] || fail "the bundle ships juvin-host.dll"
pass "Info.plist (minimum macOS $(plist_value LSMinimumSystemVersion)) and bundle contents"

codesign --verify --deep --strict --verbose=2 "$APP" 2>"$WORK/codesign.err" || {
    cat "$WORK/codesign.err" >&2
    fail "codesign --verify --deep --strict rejected the bundle"
}
[[ $(codesign --display --verbose=2 "$APP" 2>&1) == *"Identifier=$BUNDLE_ID"* ]] || fail "the bundle is not signed as $BUNDLE_ID"
expect_signed "$APP" executable
expect_signed "$APP/Contents/MacOS/juvin-host" executable
expect_signed "$APP/Contents/MacOS/juvin-cli" executable
expect_signed "$APP/Contents/MacOS/libAvaloniaNative.dylib"
pass "signatures verify with the hardened runtime"

# The copy carries no quarantine flag, as an image that was never downloaded does not; a real download is a manual check.
spctl --assess --type execute --verbose=4 "$APP" >"$WORK/spctl.out" 2>&1 || true
sed 's/^/  spctl: /' "$WORK/spctl.out"
pass "Gatekeeper assessment reported (not enforced)"

status=0
"$APP/Contents/MacOS/juvin-host" </dev/null 2>"$WORK/host.err" || status=$?
[[ $status -eq 1 ]] || fail "juvin-host exited with $status, expected 1"
grep -q "$HOST_MESSAGE" "$WORK/host.err" || fail "juvin-host did not report '$HOST_MESSAGE'"
"$APP/Contents/MacOS/juvin-cli" --help >/dev/null || fail "juvin-cli --help"
pass "juvin-host and juvin-cli"

"$LSREGISTER" -f "$APP" >/dev/null 2>&1 || true
# The log line, not the socket file alone: a socket left by an earlier run would be there from the start.
socket_ready() { [[ -S $SOCKET ]] && grep -q '\[App\] Control socket ready' "$APP_LOG" 2>/dev/null; }
open -n -g --stdout "$APP_LOG" --stderr "$APP_LOG" "$APP" || fail "open could not start Juvin.app"
for _ in $(seq 60); do
    PID=$(app_pid)
    if socket_ready; then
        break
    fi
    sleep 1
done
if ! socket_ready; then
    show_log
    fail "juvin did not open its control socket within 60 seconds"
fi
[[ -n $PID ]] || fail "the control socket is there but no juvin process runs from $EXE"
"$EXE" --trigger >>"$WORK/trigger.log" 2>&1 || {
    show_log
    fail "juvin --trigger failed"
}
sleep 1
kill -0 "$PID" 2>/dev/null || {
    show_log
    fail "juvin exited after the trigger"
}
pass "juvin started from Launch Services and answered --trigger"

kill -TERM "$PID"
for _ in $(seq 20); do
    kill -0 "$PID" 2>/dev/null || break
    sleep 0.5
done
if kill -0 "$PID" 2>/dev/null; then
    fail "juvin did not quit within 10 seconds of SIGTERM"
fi
grep -aiE "exception|error|fail" "$APP_LOG" | sed "s/^/  juvin log: /" >&2 || true
pass "juvin quit"

echo "All smoke tests passed."
