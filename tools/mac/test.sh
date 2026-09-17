#!/bin/bash
# macOS: plugin syntax + unit tests, then service tests (tools/mac/test.sh [--live] [--capture]).
. "$(dirname "$0")/common.sh"
cd "$PROJECT_ROOT"
for f in src/typora-plugin/*.cjs src/typora-plugin/*.js; do node --check "$f" || die "语法检查失败: $f"; done
node --test tests/editor/controller.test.cjs tests/editor/adapter.test.cjs tests/editor/launcher.test.cjs \
  tests/editor/dialogs.test.cjs tests/editor/panel-drag.test.cjs tests/editor/mac-host.test.cjs || die "编辑保护测试失败"
args=(run --project tests/service -p:TyporaAsrPlatform=mac --)
for a in "$@"; do
  case "$a" in
    --live) args+=(--live artifacts/controlled-zh.pcm);;
    *) args+=("$a");;
  esac
done
dotnet "${args[@]}" || die "服务测试失败"
