#!/bin/bash
# Shared helpers for the macOS scripts. Source it: . "$(dirname "$0")/common.sh"
set -euo pipefail
# ~/.dotnet first: a user-local .NET 10 SDK wins over an older system-wide one.
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:/opt/homebrew/bin:/usr/local/bin:/usr/local/share/dotnet:/usr/bin:/bin:/usr/sbin:/sbin:${PATH:-}"
[[ -x "$HOME/.dotnet/dotnet" ]] && export DOTNET_ROOT="$HOME/.dotnet"
export LANG="${LANG:-en_US.UTF-8}"
MAC_TOOLS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$MAC_TOOLS/../.." && pwd)"
DATA_ROOT="$PROJECT_ROOT/.asr"
LOGS="$PROJECT_ROOT/artifacts"
APP_NAME="TyporaASR Service"
APP_BUNDLE="$PROJECT_ROOT/runtime/publish-mac/$APP_NAME.app"
APP_EXECUTABLE="TyporaAsr.Service"
mkdir -p "$DATA_ROOT" "$LOGS"

die(){ echo "错误：$*" >&2; exit 1; }

# json_get <file> <key>: top-level JSON value as text ("" when missing).
json_get(){ /usr/bin/plutil -extract "$2" raw -o - "$1" 2>/dev/null || true; }

# Single startup/stop lock shared by start.sh and stop.sh (mkdir is atomic; stale locks are reclaimed).
LOCK_DIR="$DATA_ROOT/control.lock"
acquire_lock(){
  if mkdir "$LOCK_DIR" 2>/dev/null; then echo $$ > "$LOCK_DIR/pid"; trap release_lock EXIT; return 0; fi
  local owner; owner="$(cat "$LOCK_DIR/pid" 2>/dev/null || true)"
  if [[ -n "$owner" ]] && kill -0 "$owner" 2>/dev/null; then return 1; fi
  rm -rf "$LOCK_DIR"; mkdir "$LOCK_DIR" 2>/dev/null || return 1
  echo $$ > "$LOCK_DIR/pid"; trap release_lock EXIT
}
release_lock(){ rm -rf "$LOCK_DIR"; }

# Authenticated service health: prints the JSON body on success.
service_health(){
  local connection="$DATA_ROOT/connection.json" endpoint headers
  [[ -f "$connection" ]] || return 1
  endpoint="$(json_get "$connection" endpoint)"; headers="$(json_get "$connection" curlHeaderFile)"
  [[ -n "$endpoint" && -f "$headers" ]] || return 1
  curl -sf --noproxy '*' -m "${1:-2}" -H @"$headers" "$endpoint/health"
}

model_ready(){ curl -sf --noproxy '*' -m 2 "$1/health" 2>/dev/null | grep -q '"status" *: *"ok"'; }

# Process identity used to avoid killing a recycled pid.
proc_start(){ ps -o lstart= -p "$1" 2>/dev/null | sed 's/^ *//;s/ *$//' || true; }
proc_path(){ ps -o comm= -p "$1" 2>/dev/null | sed 's/^ *//;s/ *$//' || true; }
