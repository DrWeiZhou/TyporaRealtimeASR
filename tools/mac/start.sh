#!/bin/bash
# macOS: start the local Qwen3-ASR model (llama-server) and the transcription service.
# Usage: tools/mac/start.sh [--skip-model]
. "$(dirname "$0")/common.sh"
SKIP_MODEL=0
[[ "${1:-}" == "--skip-model" ]] && SKIP_MODEL=1
# Keep the caller's stderr on fd 3 for the final error message; everything else goes to the log.
exec 3>&2 >>"$LOGS/start.log" 2>&1 </dev/null
echo "==== $(date '+%F %T') start ===="
fail(){ echo "错误：$*"; echo "错误：$*" >&3; exit 1; }
acquire_lock || fail "另一窗口正在启动或终止服务，请稍后检查服务状态"

CONFIG="$PROJECT_ROOT/config.local.json"
if [[ ! -f "$CONFIG" ]]; then cp "$PROJECT_ROOT/config.example.mac.json" "$CONFIG"; echo "已根据示例创建 config.local.json（使用本地模型时请填写模型与 llama-server 路径）。"; fi
ENDPOINT="$(json_get "$CONFIG" asrEndpoint)"; ENDPOINT="${ENDPOINT:-http://127.0.0.1:18081}"

ready=0
online="$(json_get "$DATA_ROOT/asr-mode.json" online)"
if [[ "$online" == "true" || "$online" == "1" ]]; then ready=1; echo "已启用在线 ASR，跳过本地模型。"
elif model_ready "$ENDPOINT"; then ready=1; fi

if [[ $ready -eq 0 && $SKIP_MODEL -eq 0 ]]; then
  [[ "$ENDPOINT" == "http://127.0.0.1:18081" ]] || fail "自定义模型端点请先自行启动，然后使用 --skip-model"
  MODEL="$(json_get "$CONFIG" model)"; MMPROJ="$(json_get "$CONFIG" mmproj)"; DEVICE="$(json_get "$CONFIG" device)"
  SERVER="$(json_get "$CONFIG" llamaServer)"
  if [[ -z "$SERVER" ]]; then dir="$(json_get "$CONFIG" llamaDirectory)"; [[ -n "$dir" ]] && SERVER="$dir/llama-server"; fi
  [[ -z "$SERVER" ]] && SERVER="$(command -v llama-server || true)"
  missing=""
  for asset in "$MODEL" "$MMPROJ"; do [[ -f "$asset" ]] || missing="找不到模型文件 $asset"; done
  [[ -x "$SERVER" ]] || missing="找不到 llama-server（可用 brew install llama.cpp 安装，或在 config.local.json 填写 llamaServer）"
  if [[ -n "$missing" ]]; then
    # Still start the transcription service so the panel can be used to configure online ASR.
    echo "警告：${missing}；本地模型未启动，仅启动转写服务。"
    SKIP_MODEL=1
  fi
fi
if [[ $ready -eq 0 && $SKIP_MODEL -eq 0 ]]; then
  THREADS="$(sysctl -n hw.perflevel0.physicalcpu 2>/dev/null || sysctl -n hw.physicalcpu 2>/dev/null || echo 8)"
  args=(-m "$MODEL" --mmproj "$MMPROJ" --alias qwen3-asr --host 127.0.0.1 --port 18081 -c 4096 -np 1 -ngl 99 -b 256 -ub 256 -t "$THREADS" --no-webui)
  # Empty device: llama.cpp picks Metal on Apple Silicon.
  [[ -n "$DEVICE" ]] && args+=(--device "$DEVICE" --mmproj-device "$DEVICE")
  # Background children must not inherit fd 3, or the caller (Typora) would wait for the model to exit.
  nohup "$SERVER" "${args[@]}" >"$LOGS/model.stdout.log" 2>"$LOGS/model.stderr.log" </dev/null 3>&- &
  pid=$!
  sleep 0.3
  printf 'pid=%s\nstarted=%s\npath=%s\n' "$pid" "$(proc_start "$pid")" "$(proc_path "$pid")" >"$DATA_ROOT/model-process.mac"
  echo "$pid" >"$DATA_ROOT/model.pid"
  for _ in $(seq 1 90); do
    if model_ready "$ENDPOINT"; then ready=1; break; fi
    kill -0 "$pid" 2>/dev/null || break
    sleep 1
  done
fi
[[ $ready -eq 1 || $SKIP_MODEL -eq 1 ]] || fail "模型未就绪，请检查 artifacts/model.stderr.log"

if service_health >/dev/null 2>&1; then echo "服务已经运行。"; exit 0; fi

if [[ -d "$APP_BUNDLE" ]]; then
  # Launch as its own app so macOS asks for microphone access for "TyporaASR Service".
  open -g -j -n --stdout "$LOGS/service.stdout.log" --stderr "$LOGS/service.stderr.log" "$APP_BUNDLE" \
    --args --DataRoot "$DATA_ROOT" --AsrEndpoint "$ENDPOINT" --contentRoot "$PROJECT_ROOT" \
    || fail "无法启动 $APP_NAME.app"
else
  command -v dotnet >/dev/null || fail "未找到发布版服务（runtime/publish-mac），也没有 .NET 10 SDK；请先运行 tools/mac/publish.sh"
  echo "未找到发布版，使用 dotnet 构建开发版服务…"
  dotnet build "$PROJECT_ROOT/src/local-service" -v quiet -p:TyporaAsrPlatform=mac || fail "服务构建失败"
  DLL="$PROJECT_ROOT/src/local-service/bin/Debug/net10.0/TyporaAsr.Service.dll"
  ( cd "$PROJECT_ROOT" && nohup dotnet "$DLL" --DataRoot "$DATA_ROOT" --AsrEndpoint "$ENDPOINT" \
      >"$LOGS/service.stdout.log" 2>"$LOGS/service.stderr.log" </dev/null 3>&- & echo $! >"$DATA_ROOT/service.pid" )
fi

for _ in $(seq 1 40); do
  sleep 0.5
  if service_health 1 >/dev/null 2>&1; then echo "本地模型与转写服务就绪。请在 Typora 面板点击开始录音。"; exit 0; fi
done
fail "服务启动失败，请查看 artifacts/service.stderr.log"
