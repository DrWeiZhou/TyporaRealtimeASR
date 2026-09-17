#!/bin/bash
# macOS: save recordings, stop the transcription service, then stop the model this project started.
. "$(dirname "$0")/common.sh"
fail(){ echo "错误：$*" >&2; echo "$(date '+%F %T') $*" >>"$LOGS/stop.stderr.log"; exit 1; }
acquire_lock || fail "服务正在启动或终止，请稍后重试"
CONNECTION="$DATA_ROOT/connection.json"
if [[ -f "$CONNECTION" ]]; then
  ENDPOINT="$(json_get "$CONNECTION" endpoint)"; HEADERS="$(json_get "$CONNECTION" curlHeaderFile)"
  if health="$(service_health 3)"; then
    echo "$health" | grep -q '"canShutdown" *: *true' || fail "运行中的旧版服务不支持安全终止，请先升级服务"
    spid="$(echo "$health" | sed -n 's/.*"processId" *: *\([0-9][0-9]*\).*/\1/p')"
    [[ -n "$spid" ]] || fail "无法确定服务进程"
    curl -sf --noproxy '*' -m 30 -X POST -H @"$HEADERS" -H 'Content-Type: application/json' --data '{}' "$ENDPOINT/shutdown" >/dev/null || fail "服务拒绝终止请求"
    for _ in $(seq 1 90); do kill -0 "$spid" 2>/dev/null || break; sleep 0.5; done
    kill -0 "$spid" 2>/dev/null && fail "转写服务仍在保存，请稍后重试；未强制结束进程"
  else
    # A refusal means offline; a listening port that fails authentication must not lose its model.
    port="${ENDPOINT##*:}"; port="${port%%/*}"
    if [[ -n "$port" ]] && nc -z 127.0.0.1 "$port" 2>/dev/null; then fail "服务仍在监听但无法验证身份，取消终止"; fi
  fi
fi
RECORD="$DATA_ROOT/model-process.mac"
if [[ -f "$RECORD" ]]; then
  mpid="$(sed -n 's/^pid=//p' "$RECORD")"; started="$(sed -n 's/^started=//p' "$RECORD")"; mpath="$(sed -n 's/^path=//p' "$RECORD")"
  if [[ -n "$mpid" ]] && kill -0 "$mpid" 2>/dev/null && [[ "$(proc_start "$mpid")" == "$started" && "$(proc_path "$mpid")" == "$mpath" ]]; then
    kill "$mpid"
    for _ in $(seq 1 20); do kill -0 "$mpid" 2>/dev/null || break; sleep 0.5; done
    kill -0 "$mpid" 2>/dev/null && fail "模型进程尚未退出"
  fi
  rm -f "$RECORD"
fi
echo "转写服务已终止；本项目启动的模型已关闭。"
