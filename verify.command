#!/bin/bash
# macOS：双击运行自检（环境、Typora 接口探测、编译与测试），报告写入 artifacts/mac-verify.log。
cd "$(dirname "$0")" || exit 1
bash tools/mac/verify.sh
