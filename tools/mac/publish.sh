#!/bin/bash
# macOS: publish the self-contained service as "TyporaASR Service.app" (single-file, ad-hoc signed).
# Usage: tools/mac/publish.sh [output-dir] [rid]   (rid defaults to this Mac: osx-arm64 / osx-x64)
. "$(dirname "$0")/common.sh"
OUT="${1:-$PROJECT_ROOT/runtime/publish-mac}"
case "${2:-$(uname -m)}" in arm64|osx-arm64) RID=osx-arm64;; x86_64|osx-x64) RID=osx-x64;; *) die "未知架构 ${2:-}";; esac
command -v dotnet >/dev/null || die "需要 .NET 10 SDK（brew install --cask dotnet-sdk 或 https://dotnet.microsoft.com/download）"
BUILD="$PROJECT_ROOT/runtime/build-mac-$RID"
rm -rf "$BUILD"
dotnet publish "$PROJECT_ROOT/src/local-service" -c Release -r "$RID" --self-contained true \
  -p:TyporaAsrPlatform=mac -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -o "$BUILD" -v minimal || die "打包失败；运行中的发布版服务需要先停止"
VERSION="${TYPORA_ASR_VERSION:-$(cat "$PROJECT_ROOT/VERSION.txt" 2>/dev/null || echo 0.4.0)}"
APP="$OUT/$APP_NAME.app"
STAGE="$OUT/.staging-$$.app"
rm -rf "$OUT"/.staging-*.app; mkdir -p "$STAGE/Contents/MacOS" "$STAGE/Contents/Resources"
cp "$BUILD/$APP_EXECUTABLE" "$STAGE/Contents/MacOS/$APP_EXECUTABLE"
# Single-file publish leaves only helper data (e.g. static web asset manifests, unused here) besides the executable.
# Only extra native libraries may live next to it, each signed, so the bundle signature stays valid.
for extra in "$BUILD"/*; do
  name="$(basename "$extra")"
  [[ -f "$extra" && "$name" != "$APP_EXECUTABLE" && "$name" != *.pdb ]] || continue
  if file "$extra" | grep -q Mach-O; then
    cp "$extra" "$STAGE/Contents/MacOS/" && codesign --force --sign - --timestamp=none "$STAGE/Contents/MacOS/$name"
  fi
done
cat >"$STAGE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>io.github.drweizhou.typora-realtime-asr.service</string>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleExecutable</key><string>$APP_EXECUTABLE</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSAppleEventsUsageDescription</key><string>TyporaASR 需要让 Typora 保存正在记录的文档。</string>
  <key>NSMicrophoneUsageDescription</key><string>TyporaASR 需要使用麦克风录音，并把语音转写到 Typora 文档中。</string>
</dict>
</plist>
PLIST
# Ad-hoc signature binds Info.plist (microphone permission is tracked per bundle identifier).
codesign --force --sign - --timestamp=none "$STAGE" || die "codesign 失败"
rm -rf "$APP"; mv "$STAGE" "$APP"
rm -rf "$BUILD"
echo "已生成：$APP"
