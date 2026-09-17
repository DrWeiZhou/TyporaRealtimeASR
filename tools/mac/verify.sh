#!/bin/bash
# macOS self-check: environment, Typora internals, build + tests. Writes artifacts/mac-verify.log (no changes to Typora).
cd "$(dirname "$0")/../.." || exit 1
mkdir -p artifacts
LOG="artifacts/mac-verify.log"
export PATH="$HOME/.dotnet:/opt/homebrew/bin:/usr/local/bin:/usr/local/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# .NET 10 SDK: install per-user into ~/.dotnet when missing (official dotnet-install.sh; no admin rights).
if ! dotnet --list-sdks 2>/dev/null | grep -q "^10\."; then
  echo "安装 .NET 10 SDK 到 ~/.dotnet …"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" 2>&1 | tail -3
fi
[[ -x "$HOME/.dotnet/dotnet" ]] && export DOTNET_ROOT="$HOME/.dotnet"
{
echo "===== $(date '+%F %T') mac verify ====="
echo "## system"; sw_vers; uname -m; sysctl -n machdep.cpu.brand_string 2>/dev/null
echo "## tools"
for t in dotnet node git llama-server brew; do printf '%-13s ' "$t"; command -v "$t" || echo "(missing)"; done
node -v 2>/dev/null; dotnet --list-sdks 2>/dev/null
echo "## typora"
APP=/Applications/Typora.app; [[ -d "$APP" ]] || APP="$HOME/Applications/Typora.app"
defaults read "$APP/Contents/Info.plist" CFBundleShortVersionString 2>/dev/null
ls "$APP/Contents/Resources" 2>/dev/null | head -30
IDX="$APP/Contents/Resources/TypeMark/index.html"
ls -l "$IDX" 2>/dev/null && { grep -o '<script[^>]*>' "$IDX" | head -20; }
touch -c "$IDX" 2>/dev/null && echo "index.html writable: yes" || echo "index.html writable: NO"
echo "## typora frontend API probe"
JSDIR="$APP/Contents/Resources/TypeMark"
for k in saveUseNode 'File.save=' '.save=function' 'controller.runCommand' 'path.readText' 'isFileLoading' 'inSavingProcess' 'addUndoForInsert' 'JSBridge' 'appVersion' 'reqnode' 'savedContent'; do
  n=$(grep -rIl --include='*.js' -F "$k" "$JSDIR" 2>/dev/null | wc -l | tr -d ' '); echo "$k: $n files"
done
grep -rIoh --include='*.js' -E '.{0,60}save:function.{0,120}' "$JSDIR" 2>/dev/null | head -5
grep -rIoh --include='*.js' -E '.{0,80}saveUseNode.{0,80}' "$JSDIR" 2>/dev/null | head -3
echo '--- save context'
grep -rIoh --include='*.js' -E 'saveUseNode:async function.{0,900}' "$JSDIR" 2>/dev/null | head -1
grep -rIoh --include='*.js' -E '.{0,300}\.save=function.{0,600}' "$JSDIR" 2>/dev/null | head -2
grep -rIoh --include='*.js' -E '.{0,200}isNode.{0,200}' "$JSDIR" 2>/dev/null | head -4
grep -rIoh --include='*.js' -E '.{0,200}path\.readText.{0,200}' "$JSDIR" 2>/dev/null | head -2
grep -rIoh --include='*.js' -E '.{0,200}controller\.runCommand.{0,300}' "$JSDIR" 2>/dev/null | head -2
grep -rIoh --include='*.js' -E '.{0,200}path\.(selectFolderOrFile|showSaveDialog).{0,300}' "$JSDIR" 2>/dev/null | head -4
grep -rIoh --include='*.js' -E '.{0,120}reqnode.{0,120}' "$JSDIR" 2>/dev/null | head -3
echo '--- js files'; ls -la "$JSDIR" "$JSDIR/appsrc" 2>/dev/null | head -30
grep -rIoh --include='*.js' -E '.{0,40}(callHandler|callSync)\("(path|controller|document)\.[a-zA-Z]+' "$JSDIR" 2>/dev/null | sed -E 's/.*(call(Handler|Sync)\("[a-zA-Z.]+).*/\1/' | sort | uniq -c | head -60
echo "## audio devices"; system_profiler SPAudioDataType 2>/dev/null | grep -E '^\s{8}[^ ].*:$|Input Channels' | head -30
echo "## plugin tests"
for f in src/typora-plugin/*.cjs src/typora-plugin/*.js; do node --check "$f" || echo "SYNTAX FAIL $f"; done
node --test --test-reporter=tap tests/editor/controller.test.cjs tests/editor/adapter.test.cjs tests/editor/launcher.test.cjs tests/editor/dialogs.test.cjs tests/editor/panel-drag.test.cjs tests/editor/mac-host.test.cjs 2>&1 | grep -E '^# (pass|fail)|^not ok'
echo "## service build"
if command -v dotnet >/dev/null; then
  dotnet build src/local-service -p:TyporaAsrPlatform=mac -v quiet -nologo 2>&1 | tail -40
  echo "## service tests (includes keychain + 2 s microphone capture)"
  dotnet run --project tests/service -p:TyporaAsrPlatform=mac -- --capture 2>&1 | tail -60
else
  echo "dotnet missing: install with 'brew install --cask dotnet-sdk'"
fi
echo "===== done $(date '+%T') ====="
} 2>&1 | tee "$LOG"
echo; echo "报告已写入 ${LOG}，可关闭此窗口。"
