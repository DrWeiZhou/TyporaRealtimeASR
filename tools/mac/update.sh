#!/bin/bash
# macOS one-step update: test → stop → publish → install plugin → start → restart Typora.
# Usage: tools/mac/update.sh [--skip-tests] [--typora PATH] [--trust-typora-version]
. "$(dirname "$0")/common.sh"
SKIP_TESTS=0; INSTALL_ARGS=(); TYPORA="/Applications/Typora.app"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-tests) SKIP_TESTS=1;;
    --typora) TYPORA="$2"; INSTALL_ARGS+=(--typora "$2"); shift;;
    --trust-typora-version) INSTALL_ARGS+=(--trust-typora-version);;
    *) die "未知参数 $1";;
  esac; shift
done
LOG="$LOGS/update.log"
exec > >(tee "$LOG") 2>&1
status=failed
trap 'echo "UPDATE-STATUS: $status"' EXIT
if [[ $SKIP_TESTS -eq 0 ]]; then echo "== 1/5 运行测试 =="; bash "$MAC_TOOLS/test.sh"; fi
echo "== 2/5 停止转写服务与模型 =="; bash "$MAC_TOOLS/stop.sh"
echo "== 3/5 发布服务 =="; bash "$MAC_TOOLS/publish.sh"
echo "== 4/5 安装 Typora 插件 =="; bash "$MAC_TOOLS/install-plugin.sh" "${INSTALL_ARGS[@]+"${INSTALL_ARGS[@]}"}"
echo "== 5/5 启动服务并重启 Typora =="
bash "$MAC_TOOLS/start.sh" || { tail -n 20 "$LOGS/start.log"; exit 1; }
tail -n 1 "$LOGS/start.log"
if pgrep -x Typora >/dev/null; then
  echo "正在退出 Typora（如有未保存内容，请在 Typora 中选择是否保存）…"
  osascript -e 'tell application "Typora" to quit' >/dev/null 2>&1 || true
  for _ in $(seq 1 240); do pgrep -x Typora >/dev/null || break; sleep 0.5; done
fi
if pgrep -x Typora >/dev/null; then echo "Typora 仍未退出，请手动完全退出后重新打开，插件更新才会生效。"
else open -a "$TYPORA" || true; fi
status=ok
echo "更新完成。"
