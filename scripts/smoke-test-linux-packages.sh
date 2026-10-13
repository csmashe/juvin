#!/usr/bin/env bash
# Checks the packages from build-linux-packages.sh: metadata and contents on this machine, then a
# real install, a run of each binary, and a headless start of the app in Ubuntu 24.04, Ubuntu 26.04, and
# Fedora containers (podman, or docker when podman is missing).
#
# Usage: scripts/smoke-test-linux-packages.sh <version> [package-dir] [--validate-only]
#   --validate-only  skip the container installs
set -euo pipefail

usage() {
    echo "Usage: $0 <version> [package-dir] [--validate-only]" >&2
    exit 2
}

VALIDATE_ONLY=false
ARGS=()
for arg in "$@"; do
    if [[ $arg == --validate-only ]]; then
        VALIDATE_ONLY=true
    else
        ARGS+=("$arg")
    fi
done
[[ ${#ARGS[@]} -ge 1 && ${#ARGS[@]} -le 2 ]] || usage
VERSION=${ARGS[0]}
PKG_VERSION=${VERSION/-/\~}
REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PKG_DIR=$(cd "${ARGS[1]:-$REPO_ROOT/dist}" && pwd)

DEB=juvin_${VERSION}_amd64.deb
RPM=juvin-$PKG_VERSION-1.x86_64.rpm
APPIMAGE=Juvin-$VERSION-x86_64.AppImage
HOST_MESSAGE="could not connect to Juvin"
INSTALLED_PATHS=(
    opt/juvin/juvin
    opt/juvin/juvin-host
    opt/juvin/juvin-cli
    usr/bin/juvin
    usr/share/applications/juvin.desktop
    usr/share/icons/hicolor/512x512/apps/juvin.png
)

fail() {
    echo "FAIL: $*" >&2
    exit 1
}
pass() { echo "ok: $*"; }

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# Runs juvin-host with no app listening; it must exit 1 with its "not running" message. Private
# runtime and data directories keep it from reaching a Juvin that is running on this machine.
check_host_without_app() {
    local status=0
    mkdir -p -m 0700 "$WORK/run" "$WORK/data"
    XDG_RUNTIME_DIR=$WORK/run XDG_DATA_HOME=$WORK/data "$1" </dev/null 2>"$WORK/host.err" || status=$?
    [[ $status -eq 1 ]] || fail "$1 exited with $status, expected 1"
    grep -q "$HOST_MESSAGE" "$WORK/host.err" || fail "$1 did not report '$HOST_MESSAGE'"
}

for package in "$DEB" "$RPM" "$APPIMAGE"; do
    [[ -s $PKG_DIR/$package ]] || fail "$PKG_DIR/$package is missing or empty"
done
pass "all three packages exist"

dpkg-deb --info "$PKG_DIR/$DEB" >"$WORK/deb.info"
grep -qx ' Package: juvin' "$WORK/deb.info" || fail "deb package name"
grep -qxF " Version: $PKG_VERSION" "$WORK/deb.info" || fail "deb version is not $PKG_VERSION"
dpkg-deb --contents "$PKG_DIR/$DEB" | awk '{print $NF}' >"$WORK/deb.files"
for path in "${INSTALLED_PATHS[@]}"; do
    grep -qxF "./$path" "$WORK/deb.files" || fail "deb lacks /$path"
done
! grep -q 'juvin-host\.dll' "$WORK/deb.files" || fail "deb ships juvin-host.dll"
pass "deb metadata and contents"

[[ $(rpm -qp --queryformat '%{NAME} %{VERSION}' "$PKG_DIR/$RPM") == "juvin $PKG_VERSION" ]] || fail "rpm name or version"
rpm -qpl "$PKG_DIR/$RPM" >"$WORK/rpm.files"
for path in "${INSTALLED_PATHS[@]}"; do
    grep -qxF "/$path" "$WORK/rpm.files" || fail "rpm lacks /$path"
done
! grep -q 'juvin-host\.dll' "$WORK/rpm.files" || fail "rpm ships juvin-host.dll"
rpm -qp --requires "$PKG_DIR/$RPM" >"$WORK/rpm.requires"
grep -q '^libc\.so\.6(GLIBC_' "$WORK/rpm.requires" || fail "rpm does not require a glibc version"
grep -q '^libicu' "$WORK/rpm.requires" || fail "rpm does not require ICU"
pass "rpm metadata and contents"

(cd "$WORK" && "$PKG_DIR/$APPIMAGE" --appimage-extract >/dev/null)
ROOT=$WORK/squashfs-root
for path in AppRun usr/bin/juvin usr/bin/juvin-host usr/bin/juvin-cli juvin.desktop; do
    [[ -e $ROOT/$path ]] || fail "AppImage lacks $path"
done
if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$ROOT/juvin.desktop" || fail "AppImage desktop entry does not validate"
fi
check_host_without_app "$ROOT/usr/bin/juvin-host"
"$ROOT/usr/bin/juvin-cli" --help >/dev/null || fail "AppImage juvin-cli --help"
pass "AppImage contents, juvin-host, and juvin-cli"

if $VALIDATE_ONLY; then
    echo "Skipped the container installs (--validate-only)."
    exit 0
fi

if command -v podman >/dev/null 2>&1; then
    ENGINE=podman
elif command -v docker >/dev/null 2>&1; then
    ENGINE=docker
else
    fail "neither podman nor docker was found; install one or pass --validate-only"
fi

# Runs inside each container after the package is installed: the binaries, then the app headless
# until its control socket answers --trigger.
cat >"$WORK/probe.sh" <<EOF
#!/bin/bash
set -euo pipefail
status=0
/opt/juvin/juvin-host </dev/null 2>/tmp/host.err || status=\$?
[ "\$status" -eq 1 ] || { echo "juvin-host exited with \$status, expected 1" >&2; exit 1; }
grep -q "$HOST_MESSAGE" /tmp/host.err || { echo "juvin-host did not report '$HOST_MESSAGE'" >&2; exit 1; }
juvin-cli --help >/dev/null
echo "ok: juvin-host and juvin-cli"

base=\$(mktemp -d)
mkdir -p -m 0700 "\$base/run"
mkdir -p "\$base/data" "\$base/config" "\$base/home"
export XDG_RUNTIME_DIR=\$base/run XDG_DATA_HOME=\$base/data XDG_CONFIG_HOME=\$base/config HOME=\$base/home
xvfb-run -a dbus-run-session -- bash /probe-gui.sh "\$base"
EOF

cat >"$WORK/probe-gui.sh" <<'EOF'
#!/bin/bash
set -euo pipefail
log=$1/juvin.log
socket=$XDG_RUNTIME_DIR/juvin/control.sock
juvin >"$log" 2>&1 &
pid=$!
for _ in $(seq 60); do
    [ -S "$socket" ] && break
    if ! kill -0 "$pid" 2>/dev/null; then
        echo "juvin exited before its control socket appeared:" >&2
        cat "$log" >&2
        exit 1
    fi
    sleep 1
done
if [ ! -S "$socket" ]; then
    echo "juvin did not create $socket within 60 seconds:" >&2
    cat "$log" >&2
    exit 1
fi
juvin --trigger || { echo "juvin --trigger failed" >&2; cat "$log" >&2; exit 1; }
if ! kill -0 "$pid" 2>/dev/null; then
    echo "juvin crashed after the trigger:" >&2
    cat "$log" >&2
    exit 1
fi
kill -TERM "$pid"
wait "$pid" || true
grep -aiE "exception|error|fail" "$log" | sed "s/^/  juvin log: /" >&2 || true
echo "ok: juvin started headless and answered --trigger"
EOF

run_container() {
    local image=$1 script=$2
    echo "==> $image"
    "$ENGINE" run --rm \
        -v "$PKG_DIR:/pkg:ro,z" \
        -v "$WORK/probe.sh:/probe.sh:ro,z" \
        -v "$WORK/probe-gui.sh:/probe-gui.sh:ro,z" \
        "$image" bash -euo pipefail -c "$script" || fail "$image"
}

for ubuntu in 24.04 26.04; do
    run_container "docker.io/library/ubuntu:$ubuntu" "
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends /pkg/$DEB >/dev/null
apt-get install -y -qq --no-install-recommends xvfb xauth dbus-daemon >/dev/null
bash /probe.sh
apt-get remove -y -qq juvin >/dev/null
[ ! -e /opt/juvin ] || { echo '/opt/juvin is still there after remove' >&2; exit 1; }
echo 'ok: removed'
"
    pass "Ubuntu $ubuntu install, run, and remove"
done

run_container registry.fedoraproject.org/fedora:42 "
dnf install -y -q --setopt=install_weak_deps=False /pkg/$RPM >/dev/null
dnf install -y -q --setopt=install_weak_deps=False xorg-x11-server-Xvfb xauth dbus-daemon >/dev/null 2>&1
bash /probe.sh
dnf remove -y -q juvin >/dev/null 2>&1
[ ! -e /opt/juvin ] || { echo '/opt/juvin is still there after remove' >&2; exit 1; }
echo 'ok: removed'
"
pass "Fedora 42 install, run, and remove"

echo "All smoke tests passed."
