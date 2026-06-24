#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PID_DIR="$ROOT_DIR/.local-run/pids"

stop_service() {
  local name="$1"
  local pid_file="$PID_DIR/$name.pid"

  if [[ ! -f "$pid_file" ]]; then
    echo "$name is not running (no pid file)."
    return 0
  fi

  local pid
  pid="$(cat "$pid_file")"

  if kill -0 "$pid" >/dev/null 2>&1; then
    kill "$pid" >/dev/null 2>&1 || true

    local tries=20
    while kill -0 "$pid" >/dev/null 2>&1 && [[ "$tries" -gt 0 ]]; do
      sleep 0.5
      tries=$((tries - 1))
    done

    if kill -0 "$pid" >/dev/null 2>&1; then
      kill -9 "$pid" >/dev/null 2>&1 || true
    fi

    echo "Stopped $name (PID $pid)"
  else
    echo "$name pid file existed but process $pid was not running."
  fi

  rm -f "$pid_file"
}

if [[ ! -d "$PID_DIR" ]]; then
  echo "No PID directory found. Nothing to stop."
  exit 0
fi

stop_service "demo-client"
stop_service "gateway"
stop_service "mock-pds"
stop_service "mock-oidc"

echo "Local services stopped."
