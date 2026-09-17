#!/bin/bash
# macOS: install / uninstall the Typora plugin loader.
# Usage: tools/mac/install-plugin.sh [--typora /Applications/Typora.app] [--trust-typora-version] [--uninstall]
. "$(dirname "$0")/common.sh"
TYPORA=""; UNINSTALL=0; TRUST=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --typora|-p) TYPORA="$2"; shift;;
    --uninstall) UNINSTALL=1;;
    --trust-typora-version) TRUST=1;;
    *) die "未知参数 $1";;
  esac; shift
done
if [[ -z "$TYPORA" ]]; then
  for c in "/Applications/Typora.app" "$HOME/Applications/Typora.app"; do [[ -d "$c" ]] && TYPORA="$c" && break; done
fi
[[ -d "$TYPORA" ]] || die "找不到 Typora.app，请用 --typora 指定路径"
INDEX=""
for c in "$TYPORA/Contents/Resources/TypeMark/index.html" "$TYPORA/Contents/Resources/app/index.html" "$TYPORA/Contents/Resources/appsrc/index.html"; do
  [[ -f "$c" ]] && INDEX="$c" && break
done
[[ -n "$INDEX" ]] || die "找不到 Typora 的 index.html（Contents/Resources/TypeMark/index.html）"
RES="$(dirname "$INDEX")"
PACKAGE="$RES/typora-asr"
VERSION="$(/usr/bin/defaults read "$TYPORA/Contents/Info.plist" CFBundleShortVersionString 2>/dev/null || echo unknown)"

if ! touch "$INDEX" 2>/dev/null; then
  die "无法写入 ${INDEX}。请在“系统设置 → 隐私与安全性 → App 管理”中允许“终端”，或用 sudo 运行本脚本"
fi

strip_tag(){ /usr/bin/perl -0pi -e 's/<!-- typora-asr:start -->.*?<!-- typora-asr:end -->//gs' "$1"; }

# macOS checks Typora's Developer ID seal when it first launches a (new) copy. Editing index.html breaks that seal, and
# re-signing ad hoc is blocked by current macOS. So Typora must have been launched once *before* the plugin is injected
# (the check result is remembered); never re-sign the app. After every Typora update: open Typora once, quit, reinstall.
launched_before(){
  local key; key="$(/usr/bin/codesign -dv "$TYPORA" 2>&1 | sed -n 's/^TeamIdentifier=//p')"
  [[ "$key" == "9HWK5273G4" ]] || { echo "错误：Typora.app 的官方签名已不完整（TeamIdentifier=${key:-无}）。请从 typora.io 重新安装 Typora，打开一次后再运行本脚本。"; exit 1; }
}
if [[ $UNINSTALL -eq 1 ]]; then
  strip_tag "$INDEX"
  echo "已移除插件加载入口，重启 Typora 生效。录音和模型文件保留。"
  exit 0
fi

launched_before
[[ -f "$INDEX.before-asr" ]] || cp -p "$INDEX" "$INDEX.before-asr"
mkdir -p "$PACKAGE"
cp -f "$PROJECT_ROOT"/src/typora-plugin/*.cjs "$PROJECT_ROOT"/src/typora-plugin/*.js "$PACKAGE/"

VERSIONS='"1.14.10"'
if [[ "$VERSION" != "1.14.10" ]]; then
  if [[ $TRUST -eq 1 ]]; then
    VERSIONS="$VERSIONS,\"$VERSION\""
    echo "警告：Typora $VERSION 未经验证，已按 --trust-typora-version 允许自动写入。请先在测试文档中确认插入、撤销和保存正常。"
  else
    echo "提示：本机 Typora 版本为 ${VERSION}，插件只在 1.14.10 上验证过，自动写入正文会被拒绝。"
    echo "      确认要在此版本使用时，重新运行并加 --trust-typora-version。"
  fi
fi
json_escape(){ printf '%s' "$1" | sed 's/\\/\\\\/g;s/"/\\"/g'; }
cat >"$PACKAGE/config.json" <<JSON
{
  "platform": "mac",
  "connectionFile": "$(json_escape "$DATA_ROOT/connection.json")",
  "projectRoot": "$(json_escape "$PROJECT_ROOT")",
  "homeDir": "$(json_escape "$HOME")",
  "verifiedTyporaVersions": [$VERSIONS]
}
JSON

html_escape(){ printf '%s' "$1" | sed 's/&/\&amp;/g;s/"/\&quot;/g;s/</\&lt;/g;s/>/\&gt;/g'; }
TAG="<!-- typora-asr:start --><script src=\"./typora-asr/bootstrap.js\" data-asr-root=\"$(html_escape "$PACKAGE")\" defer></script><!-- typora-asr:end -->"
strip_tag "$INDEX"
grep -q '</body>' "$INDEX" || die "index.html 格式不兼容"
TAG="$TAG" /usr/bin/perl -0pi -e 's/<\/body>/$ENV{TAG}<\/body>/' "$INDEX"
echo "插件已安装到 ${PACKAGE}（Typora ${VERSION}）。完全退出并重新打开 Typora 后可看到语音记录面板。"
echo "注意：Typora 自动更新后，请先正常打开一次新版 Typora 并退出，再重新运行本脚本（不要对 Typora 重新签名）。"
