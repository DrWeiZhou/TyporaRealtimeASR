#!/bin/bash
# macOS：双击启动本地模型与转写服务（对应 Windows 的 start.cmd）。
cd "$(dirname "$0")" || exit 1
if bash tools/mac/start.sh; then tail -n 1 artifacts/start.log; else echo; echo "启动失败，最近的日志："; tail -n 20 artifacts/start.log; read -r -p "按回车键关闭…" _; exit 1; fi
