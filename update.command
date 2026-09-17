#!/bin/bash
# macOS：双击一键更新（测试 → 停止 → 发布 → 安装插件 → 启动 → 重启 Typora），对应 Windows 的 update.cmd。
cd "$(dirname "$0")" || exit 1
bash tools/mac/update.sh "$@"
read -r -p "按回车键关闭…" _
