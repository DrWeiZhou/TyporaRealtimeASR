#!/bin/bash
# macOS release zip: tools/mac/release.sh [version] [rid]
. "$(dirname "$0")/common.sh"
VERSION="${1:-0.4.0}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "Invalid release version"
case "${2:-$(uname -m)}" in arm64|osx-arm64) RID=osx-arm64; ARCH=arm64;; x86_64|osx-x64) RID=osx-x64; ARCH=x64;; *) die "未知架构";; esac
NAME="TyporaRealtimeASR-v$VERSION-macos-$ARCH"
STAGING="$PROJECT_ROOT/runtime/release-mac-$VERSION-$$"
PKG="$STAGING/$NAME"
mkdir -p "$PKG"
echo "$VERSION" >"$PKG/VERSION.txt"
TYPORA_ASR_VERSION="$VERSION" bash "$MAC_TOOLS/publish.sh" "$PKG/runtime/publish-mac" "$RID"
mkdir -p "$PKG/src" "$PKG/tools"
cp -R "$PROJECT_ROOT/src/typora-plugin" "$PKG/src/"
cp -R "$PROJECT_ROOT/tools/mac" "$PKG/tools/"
cp -R "$PROJECT_ROOT/docs" "$PKG/"
for f in README.md LICENSE config.example.mac.json start.command install.command update.command; do cp "$PROJECT_ROOT/$f" "$PKG/"; done
chmod +x "$PKG"/*.command "$PKG"/tools/mac/*.sh
ARCHIVE="$LOGS/$NAME.zip"
rm -f "$ARCHIVE"
( cd "$STAGING" && /usr/bin/ditto -c -k --sequesterRsrc --keepParent "$NAME" "$ARCHIVE" )
( cd "$LOGS" && shasum -a 256 "$NAME.zip" >"$NAME.zip.sha256" )
rm -rf "$STAGING"
echo "Release: $ARCHIVE"
