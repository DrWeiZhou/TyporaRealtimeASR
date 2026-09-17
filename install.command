#!/bin/bash
# macOS：双击安装 Typora 插件（对应 Windows 的 install.cmd）。安装前请完全退出 Typora。
cd "$(dirname "$0")" || exit 1
bash tools/mac/install-plugin.sh "$@"
read -r -p "按回车键关闭…" _
