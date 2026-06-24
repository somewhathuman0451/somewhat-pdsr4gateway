#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN_DIR="$ROOT_DIR/.local-run"
LOG_DIR="$RUN_DIR/logs"
PID_DIR="$RUN_DIR/pids"

mkdir -p "$LOG_DIR" "$PID_DIR"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is required but was not found on PATH."
  exit 1
fi

if [[ -f "$PID_DIR/mock-oidc.pid" || -f "$PID_DIR/mock-pds.pid" || -f "$PID_DIR/gateway.pid" || -f "$PID_DIR/demo-client.pid" ]]; then
  echo "Existing PID files found in $PID_DIR."
  echo "Run ./scripts/stop-local.sh first, or remove stale PID files."
  exit 1
fi

start_service() {
  local name="$1"
  local project_dir="$2"
  local log_file="$3"

  (
    cd "$project_dir"
    ASPNETCORE_ENVIRONMENT=Development nohup dotnet run --launch-profile http >"$log_file" 2>&1 &
    echo $! >"$PID_DIR/$name.pid"
  )

  local pid
  pid="$(cat "$PID_DIR/$name.pid")"
  echo "Started $name (PID $pid)"
}

wait_for_url() {
  local name="$1"
  local url="$2"
  local max_tries=60
  local i

  for ((i = 1; i <= max_tries; i++)); do
    if curl -fsS "$url" >/dev/null 2>&1; then
      echo "$name is ready at $url"
      return 0
    fi
    sleep 1
  done

  echo "$name did not become ready at $url"
  return 1
}

cleanup_on_error() {
  "$ROOT_DIR/scripts/stop-local.sh" >/dev/null 2>&1 || true
}

trap cleanup_on_error ERR

start_service "mock-oidc" "$ROOT_DIR/samples/Somewhat.PdsR4Gateway.Sample.MockOidcProvider" "$LOG_DIR/mock-oidc.log"
wait_for_url "mock-oidc" "http://localhost:5020/.well-known/openid-configuration"

start_service "mock-pds" "$ROOT_DIR/samples/Somewhat.PdsR4Gateway.Sample.MockPdsApi" "$LOG_DIR/mock-pds.log"
wait_for_url "mock-pds" "http://localhost:5252/"

start_service "gateway" "$ROOT_DIR/src/Somewhat.PdsR4Gateway" "$LOG_DIR/gateway.log"
wait_for_url "gateway" "http://localhost:5298/health"

start_service "demo-client" "$ROOT_DIR/samples/Somewhat.PdsR4Gateway.Sample" "$LOG_DIR/demo-client.log"
wait_for_url "demo-client" "http://localhost:5072/"

trap - ERR

echo
echo "All services are running:"
echo "- mock-oidc:  http://localhost:5020"
echo "- mock-pds:   http://localhost:5252"
echo "- gateway:    http://localhost:5298"
echo "- demo-client:http://localhost:5072"
echo
echo "Logs: $LOG_DIR"
echo "Stop with: ./scripts/stop-local.sh"
