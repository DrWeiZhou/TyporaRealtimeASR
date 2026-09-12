# 新项目启动指引：Windows 原生 Qwen3-ASR 实时转写与 Typora 集成

整理日期：2026-09-12。

本文件独立保存已完成的 llama.cpp 测试、复现方法和新项目实现设计。可将本文件复制到一个空目录，作为新项目的唯一初始资料；不依赖原项目的源代码、模型目录、测试录音或其他文档。

## 1. 目标与已确定的技术路线

开发一个 Windows 上使用的 Typora 语音转写插件：持续录音、实时预览、逐步将转写补充到记录.md；用户能够同时修改已有内容，自动转写不能覆盖人工修改。

确定首版采用：

- Windows 原生 llama.cpp + Vulkan，运行 Qwen3-ASR-0.6B 的 Q8_0 主模型和配套音频模型。
- Windows 本地会话服务负责录音、音频持久化、分段、识别调度、事件日志和恢复。
- Typora 自定义插件负责预览、编辑事务、人工修改保护和文档保存。
- 使用有界音频段的重复推理实现应用层实时更新，不宣称原生无限音频流推理。
- 不使用 WSL2；首版无需 Docker、Ollama 或 PyTorch 推理服务。

M4 Pro / 64GB Mac 可留作未来可替换后端，首版不依赖它。之前测试的 Ollama 社区发布包在 Ollama 0.34.0 上拒绝音频多模态请求；该结论仅针对该发布包，不意味着 Ollama 整体没有音频接口。新项目聚焦已经验证的 llama.cpp 路线。

## 2. 实测环境与固定版本

| 项目 | 实测值 |
|---|---|
| 系统 | Windows，原生 x64 进程 |
| CPU | Intel Core i7-11800H，8 核 16 线程 |
| 系统内存 | 64GB |
| GPU | NVIDIA T1200 Laptop GPU，4GB |
| NVIDIA 驱动 | 596.47 |
| llama.cpp | b10809；版本输出 0.4.0-dev；commit 5266f24da |
| 二进制版本 | Windows x64 Vulkan |
| 模型仓库 | ggml-org/Qwen3-ASR-0.6B-GGUF |
| 模型 revision | 928ab958557df9aa2ef1c93e0e83c7ad0933fae2 |
| 模型精度 | Q8_0 |
| 本地 HTTP 地址 | http://127.0.0.1:18081 |
| 模型 API 别名 | qwen3-asr |

模型来自 llama.cpp 维护组织的转换发布，原始模型来自 Qwen。必须成套使用主模型与 mmproj；只有文本主模型不能证明具备音频识别能力。

| 文件 | 字节数 | SHA256 |
|---|---:|---|
| Qwen3-ASR-0.6B-Q8_0.gguf | 804749248 | bca259818b50ca7c4c05e9bdb35a5dc04fa039653a6d6f3f0f331f96f6aa1971 |
| mmproj-Qwen3-ASR-0.6B-Q8_0.gguf | 214392480 | 41a342b5e4c514e968cb756de6cd1b7be39eff43c44c57a2ef5fc6522e36603d |
| llama-b10809-bin-win-vulkan-x64.zip | 35221385 | 97e50b3ef0cdd2cb4d5afd446a9006b3496bee6c0d0ba7083d32f36075771870 |

这两个模型文件合计约 1.02GB，指磁盘体积，不是运行显存需求。本次全部下载文件校验通过。升级二进制或模型时重新跑基准，不直接沿用旧性能结论。

## 3. 已完成的音频测试

测试时间：2026-09-12 20:33，Asia/Shanghai。

使用 Windows SAPI Huihui 生成 11.046 秒中文语音，转换成 16kHz、16bit、单声道 WAV 后，通过 input_audio 请求 llama-server。

标准文本：

> 今天我们测试本地语音识别。系统需要支持按部门查询权限。导出操作必须保留审计记录。

实际结果：

> 今天我们测试本地语音识别系统，需要支持按部门查询权限。导出操作必须保留审计记录。

去掉标点和空白后的字符错误率为 0，但句子边界和标点发生变化。此结果只能说明这段合成音频被正确识别，不能当作真实访谈平均准确率。

另外一段历史测试音频长 9.111 秒，返回：

> 大家好，今天我们开一个需求调研会议，主要讨论知识库和智能体平台的功能需求。

第二段没有建立人工标准答案，不计算字错率。新项目复现脚本仅使用可自行生成的第一段，不依赖这份历史音频。

### 3.1 耗时

| 请求 | 音频长度 | 首个输出 | 完整请求耗时 | RTF |
|---|---:|---:|---:|---:|
| 服务就绪后的首次请求 | 11.046 秒 | 6.212 秒 | 6.483 秒 | 0.587 |
| 预热后的完整请求 | 11.046 秒 | 0.259 秒 | 0.527 秒 | 0.048 |
| 历史测试音频 | 9.111 秒 | 0.756 秒 | 1.058 秒 | 0.116 |
| 前 2 秒音频 | 2 秒 | 0.121 秒 | 0.195 秒 | 0.098 |
| 前 4 秒音频 | 4 秒 | 0.201 秒 | 0.302 秒 | 0.075 |
| 前 6 秒音频 | 6 秒 | 0.212 秒 | 0.363 秒 | 0.060 |
| 前 8 秒音频 | 8 秒 | 0.303 秒 | 0.528 秒 | 0.066 |
| 最终完整音频 | 11.046 秒 | 0.315 秒 | 0.562 秒 | 0.051 |

所有 8 次请求均为 HTTP 200、finish_reason=stop，无报错；cache_prompt=false，所有 timings.cache_n=0。

计时从发起 HTTP 请求到全部文字接收结束，不含采集等待、重采样、base64 准备和 Typora 保存。首个输出可能是 language 标记，不是首个中文正文。RTF=请求耗时/音频长度；它也不是端到端麦克风延迟。

模型 health=ok 后的第一次识别仍明显偏慢，录音前需要做短音频预热。预热后的数据说明有继续实现实时更新的余量，但样本量不足以报告 P95 或保证长期稳定性。

测试后整张 GPU 总占用约 3533MiB，包含其他应用，不是本模型独占显存，也不是测得的峰值。

### 3.2 临时结果确实会变化

| 累计音频 | 返回文本 |
|---|---|
| 2 秒 | 今天我们测试本地。 |
| 4 秒 | 今天我们测试本地语音识别系统。 |
| 6 秒 | 今天我们测试本地语音识别系统，需要支持按部门查。 |
| 8 秒 | 今天我们测试本地语音识别系统，需要支持按部门查询权限。导出。 |
| 最终 | 今天我们测试本地语音识别系统，需要支持按部门查询权限。导出操作必须保留审计记录。 |

不能把每次全文直接追加，也不能按已写字符数机械截取后缀。这是插件中临时段/最终段分离的实际依据。

上述前缀请求是依次提交已有音频片段的模拟测试，未按真实麦克风采样时间播放。真实采集、VAD、两小时录音和 Typora 集成尚未验证。

## 4. 在新目录复现

### 4.1 前置条件与目录

安装与显卡匹配的 NVIDIA 驱动。测试客户端使用 Python 3.10–3.12，仅标准库；附录客户端使用 audioop，不能直接用于已移除 audioop 的 Python 3.13+。llama-server 本身不需要 Python。

在你选择的新项目根目录中运行 PowerShell：

```powershell
New-Item -ItemType Directory -Force -Path models,runtime\downloads,runtime\llama,tools,artifacts | Out-Null
```

建议目录：

```text
新项目/
  启动指引.md
  models/                 # 两个 GGUF
  runtime/llama/          # 解压后的 Windows 程序和 DLL
  runtime/downloads/      # 下载包与分片
  artifacts/              # 测试音频、结果和日志
  tools/                  # 从本文附录保存的辅助脚本
  src/                    # 后续开发时创建
    local-service/        # C# 录音、调度、事件、恢复
    typora-plugin/        # 插件与编辑器适配层
  tests/                  # 后续集成测试
```

模型、运行时、录音与测试产物通常不提交 Git。本文只是启动资料，不要求现在创建全部实现目录。

### 4.2 下载并校验

把附录 A 保存为 tools/download_assets.py，然后运行：

```powershell
$env:ASR_DOWNLOAD_PROXY = 'http://127.0.0.1:7897'
python -X utf8 tools\download_assets.py
Expand-Archive -LiteralPath runtime\downloads\llama-vulkan.zip -DestinationPath runtime\llama -Force
```

实测下载时直连出现超时，通过本机 7897 代理、8MiB 分片、12 路并发完成。辅助脚本固定版本并检查三个文件 SHA256；无代理时将脚本的代理配置设为空。此设置只影响下载，不影响本地推理。

若同样大小的分片被错误缓存，最终哈希会失败；删除本次下载的失败分片后重试，不忽略哈希错误。下载时需要额外空间保存分片与合并文件。

### 4.3 生成不依赖旧项目的测试音频

下面的 Windows SAPI 命令生成合成中文音频。电脑需有 Microsoft Huihui Desktop 语音；不存在时可使用自己的短中文 WAV，并提供对应标准文本。不同系统语音版本可能导致时长略有差异。

```powershell
$voice = New-Object -ComObject SAPI.SpVoice
$selectedVoice = $voice.GetVoices() | Where-Object { $_.GetDescription() -match 'Huihui' } | Select-Object -First 1
if ($null -eq $selectedVoice) { throw '请安装 Huihui 中文语音，或自行准备测试 WAV 与参考文本。' }
$voice.Voice = $selectedVoice
$voice.Rate = 0
$voice.Volume = 100
$stream = New-Object -ComObject SAPI.SpFileStream
$stream.Format.Type = 22
$wavPath = Join-Path (Get-Location) 'artifacts\controlled-zh.wav'
$reference = '今天我们测试本地语音识别。系统需要支持按部门查询权限。导出操作必须保留审计记录。'
$stream.Open($wavPath, 3, $false)
try {
    $voice.AudioOutputStream = $stream
    $null = $voice.Speak($reference)
} finally {
    $stream.Close()
}
$reference | Set-Content artifacts\controlled-zh-reference.txt -Encoding utf8
```

该命令输出 22050Hz、单声道、16bit WAV；附录 B 客户端会重采样为 16kHz。其简单重采样只用于复现测试，生产采集链路需使用带抗混叠滤波的可靠转换实现。

### 4.4 启动服务

先检查设备编号：

```powershell
.\runtime\llama\llama-server.exe --version
.\runtime\llama\llama-server.exe --list-devices
```

实测 Vulkan0 为 NVIDIA T1200，Vulkan1 为 Intel 核显。另一台电脑必须按实际枚举选择，不能盲用编号。

在新项目根目录运行以下前台命令，Ctrl+C 停止：

```powershell
.\runtime\llama\llama-server.exe -m .\models\Qwen3-ASR-0.6B-Q8_0.gguf --mmproj .\models\mmproj-Qwen3-ASR-0.6B-Q8_0.gguf --alias qwen3-asr --host 127.0.0.1 --port 18081 -c 4096 -np 1 -ngl 99 -b 256 -ub 256 -t 8 --device Vulkan0 --mmproj-device Vulkan0 --no-webui
```

另一终端检查：

```powershell
Invoke-RestMethod http://127.0.0.1:18081/health
```

返回 status=ok 后先做一次短音频预热，再开始正式计时。Vulkan 构建已经实测，不需要额外下载 CUDA 构建或 cudart 包。若设备或显存不符合条件，可另行测试部分卸载/CPU 配置，但本文件没有其跑分结果。

### 4.5 执行基准

把附录 B 保存为 tools/benchmark_asr.py：

```powershell
python -X utf8 tools\benchmark_asr.py --label vulkan
```

结果写入 artifacts/benchmark-vulkan.json。独立脚本包含 7 次请求：首次、预热后、2/4/6/8 秒前缀和完整最终请求；历史实测共有 8 次，多出的 9 秒旧项目音频不作为新项目依赖。客户端从 SSE 中提取正文和耗时，保留原始模型输出。

## 5. 服务接口与数据流

### 5.1 两层服务

```text
麦克风
  ↓ NAudio 采集
Windows 本地会话服务
  ├─ 原始音频保存与重放
  ├─ VAD 分段、请求排队和端点控制
  ├─ ASR 适配器 → llama-server HTTP → Qwen3-ASR
  └─ WebSocket 事件 → Typora 插件
                         ↓
                 编辑器事务 → Typora 保存记录.md
```

建议本地会话服务采用 .NET 8 + NAudio，以独立进程运行；llama-server 也独立常驻。插件不管理模型细节，不直接执行任意 shell，不在每个音频块上启动新模型进程。

启动管理记录子进程 PID 与身份，只停止自己启动的进程。后台启动时隐藏窗口。产品化服务需限制本地调用来源并校验会话令牌，避免将带文件能力的本地接口无鉴权暴露给任意网页。只监听 loopback，不对外开放。

### 5.2 已验证的 ASR 请求

```json
{
  "model": "qwen3-asr",
  "stream": true,
  "temperature": 0,
  "max_tokens": 160,
  "cache_prompt": false,
  "messages": [{
    "role": "user",
    "content": [{
      "type": "input_audio",
      "input_audio": {"data": "WAV文件的base64内容", "format": "wav"}
    }]
  }]
}
```

POST 到 /v1/chat/completions。真实请求由附录 B 自动编码，不把 WAV 路径当作音频内容。模型可能返回 language Chinese<asr_text>正文；按标记解析，保留 raw_text 用于排障。stream=true 只表示本次识别的文字流式返回，不表示同一 HTTP 请求可以无限追加音频。

max_tokens=160 是约 11 秒样本的测试配置，正式实现按段长调整并检查 finish_reason；遇到长度截断要重试或拆段，不能当作正常 final。

## 6. 实时调度方案

1. 采集 16kHz、16bit、单声道，先可靠保存录音，再异步识别。设备原始格式不一致时显式重采样。
2. 网络/内部传递音频块初始取 200ms；模型不是每块都推理。
3. 当前语音段每累计约 2 秒新音频，提交一次当前段完整快照，返回 hypothesis。
4. 同一模型单路串行调度。如果旧请求未完成，合并中间预览任务，只保留最新待处理快照；保留完整原始音频与句尾 final。
5. VAD 静音端点初始取 500–800ms。检测到句尾后对完整段识别，产生 final；该停顿阈值是待验证的初始配置。
6. 当前段先控制在约 10 秒。持续长句不得硬切后直接拼字符串；需要边界前后文与保守对齐，无法可靠对齐时保留待确认结果。扩到 20 秒以上前重新测试。
7. 每段结束释放其上下文，避免两小时录音一直重发累计全文。

采集、排队、服务接收、模型处理、文档保存分别记录进度，不共用一个采样计数。每块包含 recording_id、seq、start_sample、sample_count；音频时间按采样数计算，不能混同墙上时钟。

推荐事件定义：

| 消息 | 主要字段与含义 |
|---|---|
| start | document_id、recording_id、配置，绑定目标文档 |
| audio / audio_ack | seq、采样区间；确认已持久保存的连续位置 |
| hypothesis | segment_id、revision、完整临时文本、音频终点 |
| final | event_id、segment_id、最终文本、采样区间 |
| applied | 插件已将 event_id 应用到编辑器 |
| saved | 相关编辑版本已保存到磁盘 |
| stop / resume | 最后音频序号、最后确认事件、恢复位置 |

这些是待实现的会话协议，不是 llama-server 已提供的接口。同一 segment 的 revision 单调增加，过期结果丢弃；final 用 event_id 防止重复插入。

停止必须建立屏障：停止采集 → 收齐最后回调 → 提交不足整块的尾音 → 排空待处理音频 → 最终识别 → final 日志 → 插件应用 → Typora 保存。

## 7. 同一 Markdown 中人工编辑与自动补充

### 7.1 单一保存者

记录.md 打开后，只由 Typora 保存。本地服务写旁路录音与事件日志，绝不同时直接 append 或覆盖正文。磁盘文件锁和原子替换不能解决 Typora 内存中有未保存修改的问题。

首版只支持一个活动编辑器窗口写同一文档。检测外部文件变化或多个窗口争抢时暂停自动补写，保留待插入事件；不能宣称已经支持任意多编辑器协同。

### 7.2 Typora 适配层

可以基于 obgnail/typora_plugin 做自定义插件，但内部接口随版本变化。先固定目标 Typora/插件框架版本，验证以下能力，再进入正式实现：

- 在指定文档位置进行最小范围插入/替换，同时更新编辑器数据模型、脏标记和撤销栈。
- 保留并映射用户光标、选择区、滚动位置，不抢焦点。
- 中文输入法 composition 期间排队，结束后再补写。
- 支持保存完成状态、文件切换、另存为、文档关闭和源码模式切换。
- 普通 insertText 围绕当前选择区工作，不能直接视为任意位置事务 API；不使用仅改 DOM 或整文 reload 的捷径。

开始时绑定 document_id 与规范化绝对路径，建立独立插入锚点，默认放在“实时记录”节末尾，不跟随用户当前光标。

### 7.3 默认模式：即时预览，短句入文

- hypothesis 在侧栏或浮层替换显示。
- final 通过编辑器事务追加到插入锚点之前，节流保存。
- 已追加段落交给用户修改；后续识别不重写历史段落。
- 用户删除已入文段落或执行撤销后，记录其处理状态，不因重放而复活。

### 7.4 可选模式：当前草稿也实时写入正文

每个临时段关联一个草稿块。更新前同时检查 segment_id/revision、编辑事务来源和最后自动内容哈希，仅更新仍由 ASR 管理的最小范围。

用户修改、删除或撤销该块后，立即转为人工管理。当前段后续识别留在预览/日志，不覆盖人工内容，也不把同一句完整 final 再追加一遍。最稳妥的自动续写边界是下一语音段；要支持当前句内无缝交接，需要额外的音频与文字边界映射，首版不默认承诺。

文档结构示例：

```markdown
# 访谈记录

## 手工要点

人工整理内容。

## 实时记录

<!-- asr-segment:s001 -->
[00:00:05] 系统需要支持按部门查询权限。

<!-- asr-insert:document-uuid -->
```

HTML 注释是辅助定位手段，需验证目标 Typora 版本保存后仍保留。锚点缺失、重复、用户删除或源码模式无法映射时，暂停自动写入并保留队列，不能使用过期偏移猜测插入位置。时间标签为采样区间对应的段落时间，不是词级对齐。

## 8. 保存、恢复与失效处理

每次录音建立旁路目录：

```text
访谈/
  记录.md
  .asr/
    manifest.json
    events.jsonl
    document-checkpoint.json
    audio/recording-001.wav
```

原则：

- 先保存音频和识别事件，再通知插件；识别失效不影响继续录音。
- 内存队列有上限，初始容纳约 10–20 秒；超出后从音频日志补读，不静默丢块。
- buffer 入队后独立所有权，禁止同一数组反复覆盖后被多个队列项引用。
- 文档应用与磁盘保存分别确认。进程崩溃后结合正文 segment 标记和保存检查点恢复。
- “已插入但未收到确认”、且标记又被人工删除的情况不能无条件重放；作为待恢复项保留，不声称跨崩溃严格 exactly-once。
- Save As 更新 document_id 与路径映射；文件切换后事件仍归原目标，不写进当前任意文档。
- 暂停、轮换、停止均以明确采样边界执行；不能把排队中的旧音频误归新段。
- 长录音分文件保存并刷新文件头，异常退出时能够定位和恢复末尾音频。

## 9. 开发里程碑与验收

| 阶段 | 交付 | 验收重点 |
|---|---|---|
| A：复现基准 | 固定模型与 runtime，在新目录独立跑通 | 哈希正确、音频成功转写、记录冷/热耗时 |
| B：编辑器验证 | 用模拟事件验证 Typora 事务 | 改上文、中文输入、撤销、保存时无覆盖与错位 |
| C：录音闭环 | NAudio → 分段 → llama-server → final → md | 原始音频完整、尾音不丢、短句持续补充 |
| D：实时与恢复 | 预览、VAD、有界队列、事件日志 | 故障后无静默丢失、重复 final 不重复入文 |
| E：长期验证 | 真麦克风、噪声、两小时录音 | 队列不持续增长，内存有界，用户编辑被保留 |
| F：可选草稿 | 当前句在正文实时变化 | 用户接管后不被覆盖、不重复整句 |

测试应覆盖：

1. 断网或服务重启 30 秒后，从音频偏移恢复；模型重新加载不丢段。
2. 停止时不足一个音频块，仍得到最终结果。
3. 请求超时、finish_reason=length、空结果、重复事件与乱序 revision。
4. 撤销、改名、另存为、删除锚点、切换文件和关闭窗口。
5. 外部磁盘修改及多个窗口，不覆盖人工内容。
6. 人声重叠、噪声、长句与专业术语，不把单条 TTS 测试的 0 字错外推到真实访谈。

性能目标：模型预热后持续处理负载应低于录音产生速度；单次推理耗时最好小于刷新间隔。记录端到端首字、句尾 final、积压量和 P50/P95，不能用离线 RTF 代替这些指标。

## 10. 给新项目开发者的开始指令

> 依据本文件创建 Windows 原生 Qwen3-ASR Typora 插件项目。先复现 llama.cpp b10809 + Vulkan + 配套 Q8_0 模型基准，再验证指定 Typora 版本的最小编辑事务。后端采用有界短段重复推理与 VAD，返回 hypothesis/final；正文只通过 Typora 保存，人工修改优先。首版完成即时预览与短句入文，不默认实现当前句的无缝人工接管。保持音频独立落盘、事件去重、停止尾音处理和故障恢复。明确区分已实测结论与设计目标。不要使用 WSL2，也不要依赖原项目路径或文件。

## 11. 上游资料

- [Qwen3-ASR 原始项目](https://github.com/QwenLM/Qwen3-ASR)
- [固定版本 llama.cpp 发布](https://github.com/ggml-org/llama.cpp/releases/tag/b10809)
- [主模型和配套 mmproj](https://huggingface.co/ggml-org/Qwen3-ASR-0.6B-GGUF/tree/928ab958557df9aa2ef1c93e0e83c7ad0933fae2)
- [llama.cpp 多模态说明](https://github.com/ggml-org/llama.cpp/blob/b10809/docs/multimodal.md)
- [Typora 社区插件框架](https://github.com/obgnail/typora_plugin)

以下附录均内嵌于本文件，清理旧项目实现文件后仍可使用。附录 A/B 基于实测脚本整理，仅调整为独立目录、补充校验和去掉旧测试音频依赖；尚未在另一个新目录重新下载并完整复跑。附录 C 保留本次已完成测试的原始 JSON。

## 附录 A：下载脚本

保存为 `tools/download_assets.py`。使用 Python 标准库和 Windows 自带的 curl.exe；代理通过 `ASR_DOWNLOAD_PROXY` 指定。下载支持按块续传，合并后校验完整 SHA-256。哈希失败时不要使用产物，应删除对应分块后重试。

```python
"""Download pinned test assets in resumable, verified HTTP ranges."""
import concurrent.futures
import hashlib
import os
import pathlib
import subprocess
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
HF = 'https://huggingface.co/ggml-org/Qwen3-ASR-0.6B-GGUF/resolve/928ab958557df9aa2ef1c93e0e83c7ad0933fae2/'
ASSETS = [
    (HF+'Qwen3-ASR-0.6B-Q8_0.gguf', ROOT/'models/Qwen3-ASR-0.6B-Q8_0.gguf', 804749248,
     'bca259818b50ca7c4c05e9bdb35a5dc04fa039653a6d6f3f0f331f96f6aa1971'),
    (HF+'mmproj-Qwen3-ASR-0.6B-Q8_0.gguf', ROOT/'models/mmproj-Qwen3-ASR-0.6B-Q8_0.gguf', 214392480,
     '41a342b5e4c514e968cb756de6cd1b7be39eff43c44c57a2ef5fc6522e36603d'),
    ('https://github.com/ggml-org/llama.cpp/releases/download/b10809/llama-b10809-bin-win-vulkan-x64.zip',
     ROOT/'runtime/downloads/llama-vulkan.zip', 35221385,
     '97e50b3ef0cdd2cb4d5afd446a9006b3496bee6c0d0ba7083d32f36075771870'),
]
PROXY = os.environ.get("ASR_DOWNLOAD_PROXY", "http://127.0.0.1:7897")
CHUNK = 8*1024*1024

def part_job(url, path, start, end):
    expected = end-start+1
    if path.exists() and path.stat().st_size == expected:
        return expected
    for attempt in range(4):
        cmd = ['curl.exe'] + (['--proxy', PROXY] if PROXY else []) + ['--fail','--location',
               '--connect-timeout','20','--max-time','120','--silent','--show-error',
               '--range',f'{start}-{end}','--output',str(path),url]
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode == 0 and path.exists() and path.stat().st_size == expected:
            return expected
        time.sleep(attempt+1)
    raise RuntimeError(f'{path.name}: range download failed; {result.stderr[-200:]}')

jobs = []
for url, dest, size, sha in ASSETS:
    dest.parent.mkdir(parents=True, exist_ok=True)
    for start in range(0,size,CHUNK):
        end = min(size,start+CHUNK)-1
        jobs.append((url, dest.with_name(dest.name+f'.part-{start//CHUNK:04d}'),start,end))
done = 0
started = time.monotonic()
with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
    futures = [pool.submit(part_job,*job) for job in jobs]
    for future in concurrent.futures.as_completed(futures):
        done += future.result()
        print(f'Downloaded {done/1024**2:.1f} MiB / {sum(a[2] for a in ASSETS)/1024**2:.1f} MiB; {time.monotonic()-started:.1f}s',flush=True)
for url, dest, size, sha in ASSETS:
    digest = hashlib.sha256()
    with dest.open('wb') as output:
        for i in range((size+CHUNK-1)//CHUNK):
            part = dest.with_name(dest.name+f'.part-{i:04d}')
            data = part.read_bytes()
            digest.update(data)
            output.write(data)
    actual = digest.hexdigest()
    if sha and actual != sha:
        raise RuntimeError(f'Hash mismatch: {dest.name}: {actual}')
    print(f'VERIFIED {dest.name}: {size} bytes sha256={actual}',flush=True)
```

## 附录 B：基准测试脚本

保存为 `tools/benchmark_asr.py`，使用 Python 3.10–3.12。先按正文生成测试音频并启动服务。正常执行包含 7 次请求，`--quick` 只执行 2 次；请求失败、空结果或非正常结束会返回非零退出码。CER 是观测指标，不作为脚本退出条件。

```python
"""Measure local llama-server audio transcription and growing-prefix latency."""
import argparse
import audioop
import base64
import datetime
import io
import json
import pathlib
import time
import unicodedata
import urllib.error
import urllib.request
import wave

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts'
parser = argparse.ArgumentParser()
parser.add_argument('--port',type=int,default=18081)
parser.add_argument('--label',default='vulkan')
parser.add_argument('--quick',action='store_true')
args = parser.parse_args()
reference = (OUT/'controlled-zh-reference.txt').read_text(encoding='utf-8-sig').strip()
report = dict(tested_at=datetime.datetime.now().astimezone().isoformat(),backend=args.label,
              reference=reference, tests=[], note='Synthetic speech; growing-prefix requests are simulated streaming, not native incremental audio.')
target = OUT/f'benchmark-{args.label}.json'

def wav16(path):
    with wave.open(str(path)) as w:
        pcm = w.readframes(w.getnframes())
        assert w.getnchannels()==1 and w.getsampwidth()==2
        if w.getframerate()!=16000:
            pcm,_ = audioop.ratecv(pcm,2,1,w.getframerate(),16000,None)
        return pcm

def encode(pcm):
    buf=io.BytesIO()
    with wave.open(buf,'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000); w.writeframes(pcm)
    return base64.b64encode(buf.getvalue()).decode()

def normalized(text):
    return ''.join(c.lower() for c in text if not unicodedata.category(c).startswith(('P','Z')) and not c.isspace())

def distance(a,b):
    row=list(range(len(b)+1))
    for i,x in enumerate(a,1):
        prev=row; row=[i]
        for j,y in enumerate(b,1):
            row.append(min(row[-1]+1,prev[j]+1,prev[j-1]+(x!=y)))
    return row[-1]

def run(name,pcm,ref=None):
    body=dict(model='qwen3-asr',stream=True,temperature=0,max_tokens=160,cache_prompt=False,
              messages=[dict(role='user',content=[dict(type='input_audio',input_audio=dict(data=encode(pcm),format='wav'))])])
    req=urllib.request.Request(f'http://127.0.0.1:{args.port}/v1/chat/completions',
        data=json.dumps(body).encode(),headers={'Content-Type':'application/json'})
    started=time.perf_counter(); first=None; pieces=[]; timings=None; finish=None; error=None; status=None
    try:
        with urllib.request.urlopen(req,timeout=180) as response:
            status=response.status
            for line in response:
                if not line.startswith(b'data: '): continue
                data=line[6:].strip()
                if data==b'[DONE]': break
                event=json.loads(data)
                if event.get('timings'): timings=event['timings']
                for choice in event.get('choices',[]):
                    text=choice.get('delta',{}).get('content') or ''
                    if text:
                        if first is None: first=time.perf_counter()-started
                        pieces.append(text)
                    if choice.get('finish_reason'): finish=choice['finish_reason']
    except urllib.error.HTTPError as e:
        status=e.code; error=e.read().decode()
    except Exception as e: error=str(e)
    elapsed=time.perf_counter()-started
    raw=''.join(pieces)
    text=raw.split('<asr_text>',1)[-1].strip() if '<asr_text>' in raw else raw.strip()
    seconds=len(pcm)/32000
    item=dict(name=name,status=status,audio_seconds=round(seconds,3),wall_seconds=round(elapsed,3),
              first_output_seconds=round(first,3) if first is not None else None,
              rtf=round(elapsed/seconds,3),raw_text=raw,text=text,finish_reason=finish,timings=timings,error=error)
    if ref:
        item['reference']=ref
        item['normalized_cer']=round(distance(normalized(ref),normalized(text))/len(normalized(ref)),4)
    report['tests'].append(item)
    target.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(item,ensure_ascii=False),flush=True)
    return status==200 and error is None and finish=='stop' and bool(text)

pcm=wav16(OUT/'controlled-zh.wav')
if not run('controlled_full_first',pcm,reference): raise SystemExit(1)
run('controlled_full_warm',pcm,reference)
if not args.quick:
    for duration in (2,4,6,8):
        if not run(f'growing_prefix_{duration}s',pcm[:duration*32000]): break
    run('growing_prefix_final',pcm,reference)
print('Results: '+str(target),flush=True)

expected = 2 if args.quick else 7
if len(report['tests']) != expected or any(t['status'] != 200 or t['error'] or t['finish_reason'] != 'stop' or not t['text'] for t in report['tests']):
    raise SystemExit(1)
```

## 附录 C：原始实测记录

以下为清理前保存的完整 JSON，包含原项目已有音频的额外测试，因此共 8 次请求。附录 B 已移除对该音频的依赖。

```json
{
  "tested_at": "2026-09-12T20:33:39.690241+08:00",
  "backend": "vulkan",
  "reference": "今天我们测试本地语音识别。系统需要支持按部门查询权限。导出操作必须保留审计记录。",
  "tests": [
    {
      "name": "controlled_full_first",
      "status": 200,
      "audio_seconds": 11.046,
      "wall_seconds": 6.483,
      "first_output_seconds": 6.212,
      "rtf": 0.587,
      "raw_text": "language Chinese<asr_text>今天我们测试本地语音识别系统，需要支持按部门查询权限。导出操作必须保留审计记录。",
      "text": "今天我们测试本地语音识别系统，需要支持按部门查询权限。导出操作必须保留审计记录。",
      "finish_reason": "stop",
      "timings": {
        "cache_n": 0,
        "prompt_n": 166,
        "prompt_ms": 6140.004,
        "prompt_per_token_ms": 36.987975903614455,
        "prompt_per_second": 27.035813005985016,
        "predicted_n": 26,
        "predicted_ms": 271.06,
        "predicted_per_token_ms": 10.8424,
        "predicted_per_second": 92.23050247177747
      },
      "error": null,
      "reference": "今天我们测试本地语音识别。系统需要支持按部门查询权限。导出操作必须保留审计记录。",
      "normalized_cer": 0.0
    },
    {
      "name": "controlled_full_warm",
      "status": 200,
      "audio_seconds": 11.046,
      "wall_seconds": 0.527,
      "first_output_seconds": 0.259,
      "rtf": 0.048,
      "raw_text": "language Chinese<asr_text>今天我们测试本地语音识别系统，需要支持按部门查询权限。导出操作必须保留审计记录。",
      "text": "今天我们测试本地语音识别系统，需要支持按部门查询权限。导出操作必须保留审计记录。",
      "finish_reason": "stop",
      "timings": {
        "cache_n": 0,
        "prompt_n": 166,
        "prompt_ms": 235.271,
        "prompt_per_token_ms": 1.4172951807228915,
        "prompt_per_second": 705.5693221859048,
        "predicted_n": 26,
        "predicted_ms": 269.02,
        "predicted_per_token_ms": 10.7608,
        "predicted_per_second": 92.92989368820163
      },
      "error": null,
      "reference": "今天我们测试本地语音识别。系统需要支持按部门查询权限。导出操作必须保留审计记录。",
      "normalized_cer": 0.0
    },
    {
      "name": "existing_project_audio",
      "status": 200,
      "audio_seconds": 9.111,
      "wall_seconds": 1.058,
      "first_output_seconds": 0.756,
      "rtf": 0.116,
      "raw_text": "language Chinese<asr_text>大家好，今天我们开一个需求调研会议，主要讨论知识库和智能体平台的功能需求。",
      "text": "大家好，今天我们开一个需求调研会议，主要讨论知识库和智能体平台的功能需求。",
      "finish_reason": "stop",
      "timings": {
        "cache_n": 0,
        "prompt_n": 140,
        "prompt_ms": 698.502,
        "prompt_per_token_ms": 4.9893,
        "prompt_per_second": 200.42891788427235,
        "predicted_n": 25,
        "predicted_ms": 302.073,
        "predicted_per_token_ms": 12.586374999999999,
        "predicted_per_second": 79.45099363398914
      },
      "error": null
    },
    {
      "name": "growing_prefix_2s",
      "status": 200,
      "audio_seconds": 2.0,
      "wall_seconds": 0.195,
      "first_output_seconds": 0.121,
      "rtf": 0.098,
      "raw_text": "language Chinese<asr_text>今天我们测试本地。",
      "text": "今天我们测试本地。",
      "finish_reason": "stop",
      "timings": {
        "cache_n": 0,
        "prompt_n": 49,
        "prompt_ms": 90.605,
        "prompt_per_token_ms": 1.8490816326530612,
        "prompt_per_second": 540.8090061254898,
        "predicted_n": 8,
        "predicted_ms": 74.55,
        "predicted_per_token_ms": 10.65,
        "predicted_per_second": 93.89671361502347
      },
      "error": null
    },
    {
      "name": "growing_prefix_4s",
      "status": 200,
      "audio_seconds": 4.0,
      "wall_seconds": 0.302,
      "first_output_seconds": 0.201,
      "rtf": 0.075,
      "raw_text": "language Chinese<asr_text>今天我们测试本地语音识别系统。",
      "text": "今天我们测试本地语音识别系统。",
      "finish_reason": "stop",
      "timings": {
        "cache_n": 0,
        "prompt_n": 75,
        "prompt_ms": 161.115,
        "prompt_per_token_ms": 2.1482,
        "prompt_per_second": 465.5060050274648,
        "predicted_n": 11,
        "predicted_ms": 100.73,
        "predicted_per_token_ms": 10.073,
        "predicted_per_second": 99.27529038022436
      },
      "error": null
    },
    {
      "name": "growing_prefix_6s",
      "status": 200,
      "audio_seconds": 6.0,
      "wall_seconds": 0.363,
      "first_output_seconds": 0.212,
      "rtf": 0.06,
      "raw_text": "language Chinese<asr_text>今天我们测试本地语音识别系统，需要支持按部门查。",
      "text": "今天我们测试本地语音识别系统，需要支持按部门查。",
      "finish_reason": "stop",
      "timings": {
        "cache_n": 0,
        "prompt_n": 101,
        "prompt_ms": 160.778,
        "prompt_per_token_ms": 1.5918613861386137,
        "prompt_per_second": 628.1953998681412,
        "predicted_n": 17,
        "predicted_ms": 151.256,
        "predicted_per_token_ms": 9.4535,
        "predicted_per_second": 105.78092769873592
      },
      "error": null
    },
    {
      "name": "growing_prefix_8s",
      "status": 200,
      "audio_seconds": 8.0,
      "wall_seconds": 0.528,
      "first_output_seconds": 0.303,
      "rtf": 0.066,
      "raw_text": "language Chinese<asr_text>今天我们测试本地语音识别系统，需要支持按部门查询权限。导出。",
      "text": "今天我们测试本地语音识别系统，需要支持按部门查询权限。导出。",
      "finish_reason": "stop",
      "timings": {
        "cache_n": 0,
        "prompt_n": 127,
        "prompt_ms": 253.868,
        "prompt_per_token_ms": 1.99896062992126,
        "prompt_per_second": 500.25997762616794,
        "predicted_n": 21,
        "predicted_ms": 224.556,
        "predicted_per_token_ms": 11.2278,
        "predicted_per_second": 89.06464311797501
      },
      "error": null
    },
    {
      "name": "growing_prefix_final",
      "status": 200,
      "audio_seconds": 11.046,
      "wall_seconds": 0.562,
      "first_output_seconds": 0.315,
      "rtf": 0.051,
      "raw_text": "language Chinese<asr_text>今天我们测试本地语音识别系统，需要支持按部门查询权限。导出操作必须保留审计记录。",
      "text": "今天我们测试本地语音识别系统，需要支持按部门查询权限。导出操作必须保留审计记录。",
      "finish_reason": "stop",
      "timings": {
        "cache_n": 0,
        "prompt_n": 166,
        "prompt_ms": 253.921,
        "prompt_per_token_ms": 1.529644578313253,
        "prompt_per_second": 653.7466377337834,
        "predicted_n": 26,
        "predicted_ms": 247.537,
        "predicted_per_token_ms": 9.90148,
        "predicted_per_second": 100.99500276726307
      },
      "error": null,
      "reference": "今天我们测试本地语音识别。系统需要支持按部门查询权限。导出操作必须保留审计记录。",
      "normalized_cer": 0.0
    }
  ],
  "note": "Synthetic speech; growing-prefix requests are simulated streaming, not native incremental audio."
}
```
