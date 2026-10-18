#!/usr/bin/env bash
# Builds the Linux packages of Juvin: a .deb, an .rpm, and an AppImage, each with the app
# self-contained under one directory and juvin-host and juvin-cli beside it as single-file
# binaries. A format whose tool is missing (dpkg-deb, rpmbuild, wget) is skipped with a warning.
#
# Usage: scripts/build-linux-packages.sh <version> [output-dir]
#   version     MAJOR.MINOR.PATCH, optionally followed by -word or -word.number (alpha, beta.2, rc.1)
#   output-dir  where the packages go (default dist/)
set -euo pipefail

usage() {
    echo "Usage: $0 <version> [output-dir]" >&2
    exit 2
}

[[ $# -ge 1 && $# -le 2 ]] || usage
VERSION=$1
# Only prereleases of the form word or word.number are accepted: dpkg and rpm order those as SemVer does, but
# they rank other identifier mixes (and build metadata) differently, which would turn an upgrade into a downgrade.
VERSION_RE='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[A-Za-z]+(\.(0|[1-9][0-9]*))?)?$'
if [[ ! $VERSION =~ $VERSION_RE ]]; then
    echo "error: '$VERSION' is not MAJOR.MINOR.PATCH with an optional -word or -word.number prerelease" >&2
    exit 2
fi
# A '~' sorts before the release in both dpkg and rpm, as a SemVer prerelease does; '-' is not allowed in an rpm version
# and in a deb version it would start the Debian revision.
PKG_VERSION=${VERSION/-/\~}

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
mkdir -p "${2:-$REPO_ROOT/dist}"
OUT=$(cd "${2:-$REPO_ROOT/dist}" && pwd)

APPIMAGETOOL_URL=https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage
APPIMAGETOOL_SHA256=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
# Pinned so appimagetool does not fetch the moving "continuous" runtime.
RUNTIME_URL=https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64
RUNTIME_SHA256=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
TOOL_CACHE=${XDG_CACHE_HOME:-$HOME/.cache}/juvin-packaging

ICON_SIZES=(16 22 24 32 48 128 256 512)
HOST_LEFTOVERS=(juvin-host.dll juvin-host.deps.json juvin-host.runtimeconfig.json juvin-host.pdb)

# Runtime libraries, derived from the publish output (re-derive after a dependency upgrade):
#   readelf -d on every ELF file: libc.so.6, libm.so.6, libdl.so.2, libpthread.so.0, librt.so.1,
#     ld-linux-x86-64.so.2 (all glibc), libgcc_s.so.1, libstdc++.so.6, libfontconfig.so.1 (SkiaSharp),
#     libX11.so.6, libXtst.so.6, libXt.so.6, libXinerama.so.1, libxkbcommon.so.0 (libuiohook). liblttng-ust.so.0 is
#     needed only by libcoreclrtraceptprovider.so, which the runtime loads only when LTTng tracing is on.
#   P/Invoke and dlopen names in the managed assemblies (strings over *.dll):
#     Avalonia.X11: libX11.so.6, libXext.so.6, libXi.so.6, libXrandr.so.2, libXcursor.so.1,
#       libICE.so.6, libSM.so.6; optional: libGL.so.1, libvulkan.so.1, libgtk-3.so.0
#     Avalonia.OpenGL: libEGL.so.1 (optional); Avalonia.FreeDesktop.AtSpi: libdbus-1.so.3 (optional)
#     System.Security.Cryptography.Native.OpenSsl: libssl.so.3, libcrypto.so.3
#     System.Globalization: libicuuc/libicui18n (any version; the app does not use invariant globalization)
#   Commands the app runs: gdbus and gsettings (the tray probe and the GNOME shortcut; libglib2.0-bin, glib2).
DEB_DEPENDS=(
    "libgcc-s1" "libstdc++6" "zlib1g" "libssl3t64 | libssl3" "libicu78 | libicu76 | libicu74 | libicu72 | libicu70"
    "libfontconfig1" "libx11-6" "libxext6" "libxi6" "libxrandr2" "libxcursor1" "libice6" "libsm6"
    "libxtst6" "libxt6t64 | libxt6" "libxinerama1" "libxkbcommon0" "ca-certificates" "tzdata"
)
DEB_RECOMMENDS=("libgl1" "libegl1" "libdbus-1-3" "libglib2.0-bin" "xdg-utils")
RPM_REQUIRES=(
    "libgcc_s.so.1()(64bit)" "libstdc++.so.6()(64bit)" "libz.so.1()(64bit)" "libssl.so.3()(64bit)"
    "libcrypto.so.3()(64bit)" "libicu" "libfontconfig.so.1()(64bit)" "libX11.so.6()(64bit)" "libXext.so.6()(64bit)"
    "libXi.so.6()(64bit)" "libXrandr.so.2()(64bit)" "libXcursor.so.1()(64bit)" "libICE.so.6()(64bit)"
    "libSM.so.6()(64bit)" "libXtst.so.6()(64bit)" "libXt.so.6()(64bit)" "libXinerama.so.1()(64bit)" "libxkbcommon.so.0()(64bit)"
    "ca-certificates" "tzdata"
)
RPM_RECOMMENDS=("libglvnd-glx" "libglvnd-egl" "dbus-libs" "glib2" "xdg-utils")

warn() { echo "warning: $*" >&2; }
die() {
    echo "error: $*" >&2
    exit 1
}
have() { command -v "$1" >/dev/null 2>&1; }

for tool in dotnet readelf sha256sum; do
    have "$tool" || die "$tool is required"
done

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
PUBLISH=$STAGE/publish

is_elf() { [[ -f $1 ]] && [[ $(head -c4 "$1" | od -An -tx1 | tr -d ' \n') == 7f454c46 ]]; }

publish() {
    dotnet publish "$REPO_ROOT/$1" -c Release -r linux-x64 --self-contained true -p:Version="$VERSION" \
        -p:ContinuousIntegrationBuild=true --nologo "${@:2}"
}

build_payload() {
    echo "==> Publishing the app"
    if [[ -z ${JUVIN_SENTRY_DSN:-} ]]; then
        echo "    JUVIN_SENTRY_DSN is not set; crash reporting will be disabled in this build"
    fi
    local release=()
    if [[ -n ${JUVIN_RELEASE_TAG:-} ]]; then
        release=(-p:JuvinReleaseTag=true)
    fi
    publish src/Juvin.App/Juvin.App.csproj -o "$PUBLISH" -p:PublishSingleFile=false -p:PublishReadyToRun=true \
        -p:DebugType=portable -p:DebugSymbols=true -p:JuvinSentryDsn="${JUVIN_SENTRY_DSN:-}" "${release[@]}"

    local single=(-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false)
    echo "==> Publishing juvin-host and juvin-cli as single files"
    publish src/Juvin.NativeHost/Juvin.NativeHost.csproj -o "$STAGE/host" "${single[@]}"
    publish src/Juvin.Cli/Juvin.Cli.csproj -o "$STAGE/cli" "${single[@]}"

    # The app's build copies the framework-dependent host beside it; the self-contained one replaces it.
    cp "$STAGE/host/juvin-host" "$PUBLISH/juvin-host"
    cp "$STAGE/cli/juvin-cli" "$PUBLISH/juvin-cli"
    for leftover in "${HOST_LEFTOVERS[@]}"; do
        rm -f "$PUBLISH/$leftover"
    done

    if [[ -d $PUBLISH/runtimes ]]; then
        find "$PUBLISH/runtimes" -mindepth 1 -maxdepth 1 -type d ! -name linux-x64 -exec rm -rf {} +
    fi

    chmod 0755 "$PUBLISH/juvin" "$PUBLISH/juvin-host" "$PUBLISH/juvin-cli"

    for leftover in "${HOST_LEFTOVERS[@]}"; do
        [[ ! -e $PUBLISH/$leftover ]] || die "$leftover is still in the payload"
    done
    for binary in juvin-host juvin-cli; do
        is_elf "$PUBLISH/$binary" || die "$binary is not an ELF executable"
        (($(stat -c %s "$PUBLISH/$binary") > 20 * 1024 * 1024)) || die "$binary is not a self-contained single file"
    done
    [[ -f $PUBLISH/Assets/juvin-mark-color-32.png ]] || die "the tray icon is missing from the payload"
    [[ -f $PUBLISH/Resources/Localization/en.json ]] || die "the string table is missing from the payload"
}

# The highest GLIBC_x.y[.z] any ELF file in the payload needs.
glibc_floor() {
    local list=$STAGE/glibc-versions file name versions=()
    : >"$list"
    while IFS= read -r -d '' file; do
        is_elf "$file" || continue
        LC_ALL=C readelf -V --wide "$file" | awk '
            /^Version needs section/ { needs = 1; next }
            /^Version (symbols|definition) section/ { needs = 0 }
            needs && /Name: GLIBC_/ { for (i = 1; i < NF; i++) if ($i == "Name:") print $(i + 1) }' >>"$list"
    done < <(find "$PUBLISH" -type f -print0)

    while IFS= read -r name; do
        case $name in
            GLIBC_ABI_DT_RELR) versions+=(2.36) ;;
            GLIBC_[0-9]*.[0-9]*)
                [[ ${name#GLIBC_} =~ ^[0-9]+(\.[0-9]+)+$ ]] || die "unrecognised glibc version $name"
                versions+=("${name#GLIBC_}")
                ;;
            *) die "unrecognised glibc version $name" ;;
        esac
    done < <(sort -u "$list")
    ((${#versions[@]} > 0)) || die "no glibc version requirements found"
    printf '%s\n' "${versions[@]}" | sort -V | tail -n1
}

write_desktop_file() {
    cat >"$1" <<'EOF'
[Desktop Entry]
Type=Application
Name=Juvin
GenericName=Email writing assistant
Comment=Works beside you
Exec=juvin
Icon=juvin
Terminal=false
Categories=Office;
StartupNotify=false
StartupWMClass=juvin
Keywords=email;rewrite;assistant;
EOF
}

# Installs the desktop entry and icons under <root>/usr/share.
install_desktop_integration() {
    local share=$1/usr/share size
    mkdir -p "$share/applications" "$share/icons/hicolor/symbolic/apps"
    write_desktop_file "$share/applications/juvin.desktop"
    for size in "${ICON_SIZES[@]}"; do
        mkdir -p "$share/icons/hicolor/${size}x${size}/apps"
        cp "$REPO_ROOT/assets/brand/png/juvin-mark-color-$size.png" "$share/icons/hicolor/${size}x${size}/apps/juvin.png"
    done
    cp "$REPO_ROOT/assets/brand/svg/juvin-symbolic.svg" "$share/icons/hicolor/symbolic/apps/juvin-symbolic.svg"
}

# The /opt/juvin layout shared by the deb and the rpm.
stage_system_root() {
    local root=$1 name
    mkdir -p "$root/opt/juvin" "$root/usr/bin"
    cp -a "$PUBLISH/." "$root/opt/juvin/"
    for name in juvin juvin-cli; do
        printf '#!/bin/sh\nexec /opt/juvin/%s "$@"\n' "$name" >"$root/usr/bin/$name"
        chmod 0755 "$root/usr/bin/$name"
    done
    install_desktop_integration "$root"
}

# Shared by the deb and rpm install and remove hooks.
CACHE_REFRESH='if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q /usr/share/applications || true; fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true; fi'

join_by() {
    local separator=$1 first=$2
    shift 2
    printf '%s' "$first" "${@/#/$separator}"
}

build_deb() {
    local root=$STAGE/deb hook
    stage_system_root "$root"
    local installed_size
    installed_size=$(du -sk "$root" | cut -f1)
    mkdir -p "$root/DEBIAN"
    cat >"$root/DEBIAN/control" <<EOF
Package: juvin
Version: $PKG_VERSION
Section: utils
Priority: optional
Architecture: amd64
Installed-Size: $installed_size
Maintainer: Excel on the Web <noreply@excelontheweb.com>
Homepage: https://github.com/csmashe/juvin
Depends: libc6 (>= $GLIBC_FLOOR), $(join_by ', ' "${DEB_DEPENDS[@]}")
Recommends: $(join_by ', ' "${DEB_RECOMMENDS[@]}")
Description: Desktop AI assistant that rewrites email drafts
 Juvin works beside you: select a draft in your browser, press a shortcut,
 and review a rewrite made by a local model before applying it.
EOF
    printf '#!/bin/sh\nset -e\n%s\n' "$CACHE_REFRESH" >"$root/DEBIAN/postinst"
    # shellcheck disable=SC2016 # $1 belongs to the generated hook.
    printf '#!/bin/sh\nset -e\nif [ "$1" = remove ] || [ "$1" = purge ]; then\n%s\nfi\n' "$CACHE_REFRESH" >"$root/DEBIAN/postrm"
    for hook in postinst postrm; do
        chmod 0755 "$root/DEBIAN/$hook"
    done
    dpkg-deb --root-owner-group -Zxz --build "$root" "$OUT/juvin_${VERSION}_amd64.deb" >/dev/null
    BUILT+=("$OUT/juvin_${VERSION}_amd64.deb")
}

build_rpm() {
    local top=$STAGE/rpm root=$STAGE/rpm-root size
    stage_system_root "$root"
    mkdir -p "$top/SPECS" "$top/RPMS" "$top/BUILD"
    {
        cat <<EOF
%global debug_package %{nil}
%global __strip /bin/true
%global __os_install_post %{nil}
%global _build_id_links none
%global source_date_epoch_from_changelog 0

Name: juvin
Version: $PKG_VERSION
Release: 1
Summary: Desktop AI assistant that rewrites email drafts
License: GPL-3.0-or-later
URL: https://github.com/csmashe/juvin
Vendor: Excel on the Web
Packager: Excel on the Web <noreply@excelontheweb.com>
BuildArch: x86_64
AutoReqProv: no
Requires: libc.so.6()(64bit)
Requires: libc.so.6(GLIBC_$GLIBC_FLOOR)(64bit)
EOF
        printf 'Requires: %s\n' "${RPM_REQUIRES[@]}"
        printf 'Recommends: %s\n' "${RPM_RECOMMENDS[@]}"
        cat <<EOF

%description
Juvin works beside you: select a draft in your browser, press a shortcut,
and review a rewrite made by a local model before applying it.

%install
cp -a "$root/." %{buildroot}/

%post
$CACHE_REFRESH

%postun
$CACHE_REFRESH

%files
%defattr(-,root,root,-)
/opt/juvin
/usr/bin/juvin
/usr/bin/juvin-cli
/usr/share/applications/juvin.desktop
/usr/share/icons/hicolor/symbolic/apps/juvin-symbolic.svg
EOF
        for size in "${ICON_SIZES[@]}"; do
            echo "/usr/share/icons/hicolor/${size}x${size}/apps/juvin.png"
        done
    } >"$top/SPECS/juvin.spec"

    rpmbuild -bb --quiet --target x86_64 \
        --define "_topdir $top" \
        --define "_build_name_fmt %{NAME}-%{VERSION}-%{RELEASE}.%{ARCH}.rpm" \
        "$top/SPECS/juvin.spec" >/dev/null
    local rpm=juvin-$PKG_VERSION-1.x86_64.rpm
    mv "$top/RPMS/$rpm" "$OUT/$rpm"

    local floor
    floor=$(rpm -qp --requires "$OUT/$rpm" | sed -n 's/^libc\.so\.6(GLIBC_\(.*\))(64bit)$/\1/p')
    [[ $floor == "$GLIBC_FLOOR" ]] || die "the rpm glibc floor ($floor) does not match the deb's ($GLIBC_FLOOR)"
    BUILT+=("$OUT/$rpm")
}

# Downloads <url> into the cache once and checks its SHA-256 on every run.
fetch_pinned() {
    local url=$1 sha=$2 file=$TOOL_CACHE/$3
    mkdir -p "$TOOL_CACHE"
    if [[ ! -f $file ]] || ! echo "$sha  $file" | sha256sum --check --status; then
        wget -q -O "$file.part" "$url"
        mv "$file.part" "$file"
    fi
    echo "$sha  $file" | sha256sum --check --status || die "$3 does not match its pinned SHA-256"
    chmod 0755 "$file"
}

build_appimage() {
    local appdir=$STAGE/Juvin.AppDir
    fetch_pinned "$APPIMAGETOOL_URL" "$APPIMAGETOOL_SHA256" appimagetool-1.9.1-x86_64.AppImage
    fetch_pinned "$RUNTIME_URL" "$RUNTIME_SHA256" runtime-20251108-x86_64

    mkdir -p "$appdir/usr/bin"
    cp -a "$PUBLISH/." "$appdir/usr/bin/"
    install_desktop_integration "$appdir"
    write_desktop_file "$appdir/juvin.desktop"
    cp "$REPO_ROOT/assets/brand/png/juvin-mark-color-256.png" "$appdir/juvin.png"
    ln -s juvin.png "$appdir/.DirIcon"
    cat >"$appdir/AppRun" <<'EOF'
#!/bin/sh
HERE=$(dirname "$(readlink -f "$0")")
# .NET aborts without the system ICU; the AppImage does not bundle it, so fall back to invariant globalization.
ldconfig=$(command -v ldconfig || echo /sbin/ldconfig)
if ! "$ldconfig" -p 2>/dev/null | grep -q 'libicuuc\.so'; then
    export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
fi
exec "$HERE/usr/bin/juvin" "$@"
EOF
    chmod 0755 "$appdir/AppRun"

    local appimage=$OUT/Juvin-$VERSION-x86_64.AppImage
    rm -f "$appimage"
    ARCH=x86_64 "$TOOL_CACHE/appimagetool-1.9.1-x86_64.AppImage" --appimage-extract-and-run --no-appstream \
        --runtime-file "$TOOL_CACHE/runtime-20251108-x86_64" "$appdir" "$appimage" >"$STAGE/appimagetool.log" 2>&1 \
        || { cat "$STAGE/appimagetool.log" >&2; die "appimagetool failed"; }
    BUILT+=("$appimage")
}

build_payload
GLIBC_FLOOR=$(glibc_floor)
echo "==> glibc floor: $GLIBC_FLOOR"

if have desktop-file-validate; then
    write_desktop_file "$STAGE/juvin.desktop"
    desktop-file-validate "$STAGE/juvin.desktop"
fi

BUILT=()
if have dpkg-deb; then
    echo "==> Building the deb"
    build_deb
else
    warn "dpkg-deb not found; skipping the deb"
fi
if have rpmbuild; then
    echo "==> Building the rpm"
    build_rpm
else
    warn "rpmbuild not found; skipping the rpm"
fi
if have wget; then
    echo "==> Building the AppImage"
    build_appimage
else
    warn "wget not found; skipping the AppImage"
fi

echo "==> Packages in $OUT:"
for package in "${BUILT[@]}"; do
    echo "  $(basename "$package") ($(du -h "$package" | cut -f1))"
done
