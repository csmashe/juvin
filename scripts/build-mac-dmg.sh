#!/usr/bin/env bash
# Builds the macOS disk image of Juvin: Juvin.app with the app self-contained in Contents/MacOS and
# juvin-host and juvin-cli beside it as single-file binaries, signed with the hardened runtime, in a
# compressed dmg with an Applications link. Runs on macOS with its own /bin/bash 3.2.
#
# Usage: scripts/build-mac-dmg.sh <version> [output-dir]
#   version     MAJOR.MINOR.PATCH, optionally followed by -word or -word.number (alpha, beta.2, rc.1)
#   output-dir  where the dmg goes (default dist/)
# Environment:
#   JUVIN_MAC_ARCH           arm64 (default) or x64
#   JUVIN_MAC_SIGN_IDENTITY  codesign identity (default -, ad-hoc). A Developer ID Application identity also
#                            gets a secure timestamp. Next step once there is an Apple Developer account:
#                            sign the dmg itself with the same identity, then `xcrun notarytool submit <dmg>
#                            --keychain-profile <profile> --wait` and `xcrun stapler staple <dmg>`.
#   JUVIN_SENTRY_DSN, JUVIN_RELEASE_TAG  as for the other packages
set -euo pipefail

usage() {
    echo "Usage: $0 <version> [output-dir]" >&2
    exit 2
}

[[ $# -ge 1 && $# -le 2 ]] || usage
VERSION=$1
# The same versions the Linux packages accept, so one tag builds every package.
VERSION_RE='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[A-Za-z]+(\.(0|[1-9][0-9]*))?)?$'
if [[ ! $VERSION =~ $VERSION_RE ]]; then
    echo "error: '$VERSION' is not MAJOR.MINOR.PATCH with an optional -word or -word.number prerelease" >&2
    exit 2
fi
# CFBundleVersion and CFBundleShortVersionString only take numbers, so the prerelease is dropped as in the Windows file version.
NUMERIC_VERSION=${VERSION%%-*}

ARCH=${JUVIN_MAC_ARCH:-arm64}
case $ARCH in
    arm64 | x64) ;;
    *)
        echo "error: JUVIN_MAC_ARCH must be arm64 or x64, not '$ARCH'" >&2
        exit 2
        ;;
esac
RID=osx-$ARCH
SIGN_IDENTITY=${JUVIN_MAC_SIGN_IDENTITY:--}

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
mkdir -p "${2:-$REPO_ROOT/dist}"
OUT=$(cd "${2:-$REPO_ROOT/dist}" && pwd)

# Must match the label in Juvin.Platform.Mac.Launcher.LaunchAgentLoginItem.
BUNDLE_ID=com.juvin.app
ENTITLEMENTS=$REPO_ROOT/scripts/mac/entitlements.plist
HOST_LEFTOVERS="juvin-host.dll juvin-host.deps.json juvin-host.runtimeconfig.json juvin-host.pdb"

die() {
    echo "error: $*" >&2
    exit 1
}
have() { command -v "$1" >/dev/null 2>&1; }

[[ $(uname -s) == Darwin ]] || die "this script runs on macOS"
for tool in dotnet codesign hdiutil iconutil sips otool plutil file ditto; do
    have "$tool" || die "$tool is required"
done

STAGE=$(mktemp -d "${TMPDIR:-/tmp}/juvin-dmg.XXXXXX")
trap 'rm -rf "$STAGE"' EXIT
PUBLISH=$STAGE/publish
DMG_ROOT=$STAGE/dmg
APP=$DMG_ROOT/Juvin.app

is_macho() {
    [[ -f $1 ]] || return 1
    case $(file -b "$1") in
        Mach-O*) return 0 ;;
        *) return 1 ;;
    esac
}

publish() {
    local project=$1
    shift
    dotnet publish "$REPO_ROOT/$project" -c Release -r "$RID" --self-contained true -p:Version="$VERSION" \
        -p:ContinuousIntegrationBuild=true --nologo "$@"
}

build_payload() {
    echo "==> Publishing the app for $RID"
    if [[ -z ${JUVIN_SENTRY_DSN:-} ]]; then
        echo "    JUVIN_SENTRY_DSN is not set; crash reporting will be disabled in this build"
    fi
    local release=-p:JuvinReleaseTag=false
    if [[ -n ${JUVIN_RELEASE_TAG:-} ]]; then
        release=-p:JuvinReleaseTag=true
    fi
    publish src/Juvin.App/Juvin.App.csproj -o "$PUBLISH" -p:PublishSingleFile=false -p:PublishReadyToRun=true \
        -p:DebugType=portable -p:DebugSymbols=true -p:JuvinSentryDsn="${JUVIN_SENTRY_DSN:-}" "$release"

    echo "==> Publishing juvin-host and juvin-cli as single files"
    local single="-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false"
    # shellcheck disable=SC2086 # $single is a list of flags.
    publish src/Juvin.NativeHost/Juvin.NativeHost.csproj -o "$STAGE/host" $single
    # shellcheck disable=SC2086
    publish src/Juvin.Cli/Juvin.Cli.csproj -o "$STAGE/cli" $single

    # The app's build copies the framework-dependent host beside it; the self-contained one replaces it.
    cp "$STAGE/host/juvin-host" "$PUBLISH/juvin-host"
    cp "$STAGE/cli/juvin-cli" "$PUBLISH/juvin-cli"
    local leftover
    for leftover in $HOST_LEFTOVERS; do
        rm -f "$PUBLISH/$leftover"
    done

    if [[ -d $PUBLISH/runtimes ]]; then
        find "$PUBLISH/runtimes" -mindepth 1 -maxdepth 1 -type d ! -name "$RID" ! -name osx -exec rm -rf {} +
    fi

    chmod 0755 "$PUBLISH/juvin" "$PUBLISH/juvin-host" "$PUBLISH/juvin-cli"

    for leftover in $HOST_LEFTOVERS; do
        [[ ! -e $PUBLISH/$leftover ]] || die "$leftover is still in the payload"
    done
    local binary
    for binary in juvin-host juvin-cli; do
        is_macho "$PUBLISH/$binary" || die "$binary is not a Mach-O executable"
        (($(wc -c <"$PUBLISH/$binary") > 20 * 1024 * 1024)) || die "$binary is not a self-contained single file"
    done
    [[ -f $PUBLISH/libAvaloniaNative.dylib ]] || die "the Avalonia native library is missing from the payload"
    [[ -f $PUBLISH/Assets/juvin-mark-color-32.png ]] || die "the tray icon is missing from the payload"
    [[ -f $PUBLISH/Resources/Localization/en.json ]] || die "the string table is missing from the payload"
}

# The newest macOS any Mach-O file in the payload was built for, read from LC_BUILD_VERSION or LC_VERSION_MIN_MACOSX.
macos_floor() {
    local otool_arch=arm64 file
    if [[ $ARCH == x64 ]]; then
        otool_arch=x86_64
    fi
    find "$PUBLISH" -type f | while IFS= read -r file; do
        is_macho "$file" || continue
        otool -arch "$otool_arch" -l "$file" 2>/dev/null | awk '
            /cmd LC_BUILD_VERSION/ { build = 1; next }
            /cmd LC_VERSION_MIN_MACOSX/ { legacy = 1; next }
            /cmd / { build = 0; legacy = 0 }
            build && $1 == "minos" { print $2 }
            legacy && $1 == "version" { print $2 }'
    done | awk -F. '{ printf "%d.%d.%d\n", $1, $2, $3 }' | sort -t. -k1,1n -k2,2n -k3,3n | tail -n1 \
        | sed 's/\.0$//'
}

# Rendered from the color PNGs; there is no 1024 source, so the iconset stops at 512x512.
build_icon() {
    local iconset=$STAGE/juvin.iconset brand=$REPO_ROOT/assets/brand/png/juvin-mark-color
    mkdir -p "$iconset"
    cp "$brand-16.png" "$iconset/icon_16x16.png"
    cp "$brand-32.png" "$iconset/icon_16x16@2x.png"
    cp "$brand-32.png" "$iconset/icon_32x32.png"
    sips -z 64 64 "$brand-128.png" --out "$iconset/icon_32x32@2x.png" >/dev/null
    cp "$brand-128.png" "$iconset/icon_128x128.png"
    cp "$brand-256.png" "$iconset/icon_128x128@2x.png"
    cp "$brand-256.png" "$iconset/icon_256x256.png"
    cp "$brand-512.png" "$iconset/icon_256x256@2x.png"
    cp "$brand-512.png" "$iconset/icon_512x512.png"
    iconutil -c icns -o "$1" "$iconset"
}

write_info_plist() {
    # LSUIElement is left off: the settings window shows in the taskbar on Linux and Windows, so it gets a Dock icon here.
    cat >"$1" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleDisplayName</key>
    <string>Juvin</string>
    <key>CFBundleExecutable</key>
    <string>juvin</string>
    <key>CFBundleIconFile</key>
    <string>juvin.icns</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>Juvin</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$NUMERIC_VERSION</string>
    <key>CFBundleVersion</key>
    <string>$NUMERIC_VERSION</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.productivity</string>
    <key>LSMinimumSystemVersion</key>
    <string>$MACOS_FLOOR</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSLocalNetworkUsageDescription</key>
    <string>Juvin connects to the local model server you choose on your network, such as Ollama or LM Studio, to rewrite your drafts.</string>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright 2026 Excel on the Web</string>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
</dict>
</plist>
EOF
    plutil -lint "$1" >/dev/null || die "Info.plist does not lint"
}

# The whole publish output sits in Contents/MacOS because the app, the host locator, and the login item all
# resolve files next to the executable.
assemble_bundle() {
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    ditto "$PUBLISH" "$APP/Contents/MacOS"
    cp "$REPO_ROOT/LICENSE" "$APP/Contents/Resources/LICENSE"
    build_icon "$APP/Contents/Resources/juvin.icns"
    write_info_plist "$APP/Contents/Info.plist"
}

# Nested files first and the bundle last, since the bundle's seal covers the signatures inside it. codesign
# treats every file in Contents/MacOS as code, so the assemblies and assets get signatures too (kept in
# extended attributes, which the dmg and a Finder copy preserve).
sign_bundle() {
    local timestamp=--timestamp=none file
    if [[ $SIGN_IDENTITY != - ]]; then
        timestamp=--timestamp
    fi
    find "$APP/Contents/MacOS" -type f ! -path "$APP/Contents/MacOS/juvin" | while IFS= read -r file; do
        if ! is_macho "$file"; then
            codesign --force --sign "$SIGN_IDENTITY" "$timestamp" "$file"
        elif [[ $(file -b "$file") == *executable* ]]; then
            codesign --force --sign "$SIGN_IDENTITY" "$timestamp" --options runtime --entitlements "$ENTITLEMENTS" "$file"
        else
            codesign --force --sign "$SIGN_IDENTITY" "$timestamp" --options runtime "$file"
        fi
    done
    codesign --force --sign "$SIGN_IDENTITY" "$timestamp" --options runtime --entitlements "$ENTITLEMENTS" "$APP"
    codesign --verify --deep --strict "$APP" || die "the signed bundle does not verify"
}

# hdiutil on a busy machine sometimes reports the device busy; a retry gets through.
build_dmg() {
    local dmg=$1 attempt
    ln -s /Applications "$DMG_ROOT/Applications"
    rm -f "$dmg"
    for attempt in 1 2 3; do
        if hdiutil create -volname Juvin -srcfolder "$DMG_ROOT" -fs HFS+ -format UDZO -ov "$dmg" >/dev/null; then
            return 0
        fi
        echo "    hdiutil create failed (attempt $attempt); retrying"
        sleep 5
    done
    die "hdiutil could not create $dmg"
}

build_payload
MACOS_FLOOR=$(macos_floor)
[[ -n $MACOS_FLOOR ]] || die "no macOS version requirement found in the payload"
echo "==> macOS floor: $MACOS_FLOOR"

echo "==> Assembling Juvin.app"
assemble_bundle
echo "==> Signing with identity '$SIGN_IDENTITY'"
sign_bundle

DMG=$OUT/Juvin-$VERSION-$ARCH.dmg
echo "==> Building the dmg"
build_dmg "$DMG"
echo "Built $DMG ($(du -h "$DMG" | cut -f1))"
