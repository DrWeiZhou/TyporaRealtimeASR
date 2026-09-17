# macOS 版本说明

本文件说明 macOS 版如何对应 Windows 版的各项功能、如何安装，以及尚需在真机上验收的事项。

## 与 Windows 版的对应关系

| 功能 | Windows | macOS |
| --- | --- | --- |
| 麦克风录音 | NAudio WASAPI + `PcmConverter` 重采样 | Audio Queue Services（`Platform/Mac/CoreAudioCapture.cs`），队列直接输出 16 kHz 单声道 PCM16 |
| 系统声音 | WASAPI loopback | macOS 没有内置回环设备；BlackHole、Loopback 等虚拟输入设备会列在“系统声音”分组，按麦克风方式录制 |
| 设备列表 | MMDevice 枚举 | Core Audio `AudioObject` 枚举，设备 id 为 AudioDeviceID，`-1` 为系统默认输入 |
| 麦克风权限 | Windows 隐私设置 | 服务打包为 `TyporaASR Service.app`（含 `NSMicrophoneUsageDescription`），首次录音时系统弹窗；被拒绝时面板提示去“系统设置 → 隐私与安全性 → 麦克风”开启 |
| API Key 加密 | DPAPI（当前用户） | AES-256-GCM，主密钥保存在登录钥匙串（服务名 `TyporaRealtimeASR`）。Windows 加密的配置无法在 Mac 解密，请用“配置导出/导入” |
| 插件运行环境 | Electron，`window.reqnode` 提供 Node | WKWebView，没有 Node。`host-mac.cjs` 用 `window.bridge` 读文件、运行命令，提供插件需要的 fs/path/crypto/os 子集 |
| 插件访问服务 | Node `http` | `fetch()`（服务在 macOS 版仅对 Typora 页面来源开放 CORS，仍需令牌）；WebView 拒绝时改用 `curl`，令牌从服务写出的 `.asr/curl-auth.txt`（权限 0600）读取，不出现在命令行 |
| 文件写入 | 同步 `fs` | 先写内存覆盖层，再按顺序经 shell 落盘（先写临时文件再 `mv`）；保存确认前等待队列写完 |
| 保存文档 | `File.saveUseNode` 返回值 + 读回核对 | 服务 .app 通过 AppleScript 让 Typora 原生保存，再读回磁盘核对 |
| 启动/终止服务 | `start.ps1` / `stop.ps1`（PowerShell） | `tools/mac/start.sh` / `stop.sh`（bash），同样只结束本项目启动且身份匹配的模型进程 |
| 选择目录/文件 | PowerShell WinForms 对话框 | AppleScript `choose folder / choose file / choose file name` |
| 打开逐字稿 | `shell.openPath` / `cmd start` | `open` |
| 安装插件 | `install-plugin.ps1` 改写 `resources/window.html` | `install-plugin.sh` 改写 `Typora.app/Contents/Resources/TypeMark/index.html` |
| 发布 | `publish.ps1`（win-x64 自包含） | `publish.sh`（osx-arm64 / osx-x64 自包含单文件，打包为 .app 并临时签名） |
| 一键更新 / 测试 / 打包 | `update.ps1` / `test.ps1` / `release.ps1` | `update.sh` / `test.sh` / `release.sh`；根目录 `start.command`、`install.command`、`update.command` 可在访达中双击 |
| 快捷键 | Ctrl+Alt+R | Control+Option+R（⌃⌥R） |

服务端协议、SQLite 账本、分段、润色、在线 ASR、资料记录目录、配置导出/导入等逻辑两端共用同一份代码。编译时按平台选择实现：在 Mac 上构建（或 `-r osx-arm64`、`-p:TyporaAsrPlatform=mac`）得到 `net10.0` + `TYPORA_MAC`，否则仍是原来的 `net10.0-windows`。

## 准备

- macOS 13 或更新版本（Apple Silicon 或 Intel）。
- Typora for macOS。插件编辑事务只在 Typora 1.14.10 上验证过；其他版本需要安装时加 `--trust-typora-version` 才允许自动写入正文，请先用测试文档核对插入、撤销与保存。
- 从源码构建需要 .NET 10 SDK 和 Node.js（运行测试）：`brew install --cask dotnet-sdk`、`brew install node`。
- 使用本地模型时：支持音频输入的 `llama-server`（如 `brew install llama.cpp`）以及 Qwen3-ASR GGUF 主模型和 mmproj。只用在线 ASR 时不需要。

## 安装

在项目目录的终端中执行：

```bash
cp config.example.mac.json config.local.json   # 首次安装；填写 llamaServer、model、mmproj
bash tools/mac/publish.sh                      # 生成 runtime/publish-mac/TyporaASR Service.app
bash tools/mac/install-plugin.sh               # 完全退出 Typora 后执行
bash tools/mac/start.sh                        # 或在面板点“启动服务”
```

- `device` 留空时 llama.cpp 在 Apple Silicon 上默认使用 Metal；也可填 `llama-server --list-devices` 列出的名称。
- 安装前先正常打开一次 Typora 再退出；安装脚本需要写入 `Typora.app`，如提示无法写入，请在“系统设置 → 隐私与安全性 → App 管理”中允许“终端”。**不要对 Typora 重新签名**（见下方注意事项）。
- Typora 更新会覆盖 `index.html`，更新后请重新运行安装脚本。
- 卸载入口：`bash tools/mac/install-plugin.sh --uninstall`。
- 一键更新：`bash tools/mac/update.sh`（或双击 `update.command`）。

日志：`artifacts/start.log`、`artifacts/service.stderr.log`、`artifacts/model.stderr.log`、`artifacts/stop.stderr.log`。

## 开发与测试

```bash
bash tools/mac/test.sh             # 插件语法检查 + node 测试 + 服务测试
bash tools/mac/test.sh --capture   # 额外录 2 秒真实麦克风
bash tools/mac/release.sh 0.4.0    # 生成 artifacts/TyporaRealtimeASR-v0.4.0-macos-arm64.zip 及 .sha256
```

`tests/editor/mac-host.test.cjs` 用模拟的 `window.bridge` 覆盖了 macOS 插件层：文件覆盖层与顺序落盘、写入失败上报、SHA-256/UUID、fetch 与 curl 回退、bash 启动器、AppleScript 对话框分支，以及 `bootstrap.js` 在没有 Node 时的模块加载。

## 真机测试结果（2026-09-17，macOS 26.3 / Apple M4 Pro / Typora 1.14.9→1.14.10）

- 编译：`net10.0` + `TYPORA_MAC` 构建 0 错误；服务测试全部通过（含钥匙串加解密）。插件 node 测试 31 项全部通过。
- 打包：`publish.sh` 生成单文件、临时签名的 `TyporaASR Service.app`。
- 录音：经 .app 启动后系统弹出麦克风授权，允许后 5 秒收到 80242 个 16 kHz 采样；设备列表正确，BlackHole 等虚拟声卡归入“系统声音”。
- 插件：注入后面板在 Mac 版 Typora 中正常加载，fetch 连通服务，文档绑定、状态显示与快捷键正常。
- 保存：Mac 版 Typora 由原生 NSDocument 保存，页面脚本无法保存已有文件，也不能向 Typora 自身发送 Apple 事件（-1743）。现改为由 `TyporaASR Service.app` 调用 AppleScript 让 Typora 保存（`POST /mac/save-document`），首次会弹出“TyporaASR Service 想要控制 Typora”，允许后保存耗时约 0.3 秒。
- 尚未验证：带真实识别模型的完整录音→转写→润色→入文流程（本机没有 llama-server/模型，也未配置在线 ASR）。

### 已知注意事项

1. **不要对 Typora 重新签名。** 修改 `index.html` 会使 Typora 的签名封装失效；新版 macOS 会拦截本机重签名的 Typora（提示“已损坏”且无法放行，只能重装）。正确做法是：先正常打开一次官方 Typora（让系统完成首次校验）并退出，再运行 `install-plugin.sh`。Typora 每次自动更新后都要重复这一步。安装脚本会检查 Typora 仍带官方签名。
2. 安装脚本需要“系统设置 → 隐私与安全性 → App 管理”中允许“终端”。
3. 每次重新打包服务都会改变临时签名，macOS 可能再次询问麦克风和自动化权限。
4. bash 脚本中紧跟中文字符的变量必须写成 `${VAR}`，否则会被当作变量名的一部分（已统一修正）。
5. 自检脚本：双击 `verify.command`（环境、Typora 接口探测、编译与测试），报告写入 `artifacts/mac-verify.log`；服务可用 `"TyporaASR Service.app/Contents/MacOS/TyporaAsr.Service" --self-test-capture 5 report.json` 单独测试录音。

已知限制：没有内置系统声音回环（需要虚拟声卡）；插件文件写入是异步落盘，崩溃瞬间的状态保存比 Windows 版稍弱；Linux 仍未实现。
