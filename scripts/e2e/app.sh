#!/usr/bin/env bash
# Start/stop/status the PlaneWeb app for local browser testing.
# Usage: scripts/e2e/app.sh start|stop|status|log   (env: PORT, DB, ADMIN_EMAIL, ADMIN_PASSWORD)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
STATE="${E2E_STATE_DIR:-/tmp/opencode/e2e}"; mkdir -p "$STATE"
PORT="${PORT:-5099}"; DB="${DB:-$STATE/e2e.db}"
PIDFILE="$STATE/app.pid"; LOG="$STATE/app.log"
running() { [[ -f $PIDFILE ]] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; }
case "${1:-}" in
  start)
    if running; then echo "already running (pid $(cat "$PIDFILE"))"; exit 0; fi
    # Refuse to start if something else holds the port, otherwise healthz would pass against a stale instance.
    if curl -fs "http://localhost:$PORT/healthz" >/dev/null 2>&1; then
      echo "port $PORT is already serving a process not managed by this script; free it first: $0 stop" >&2; exit 1; fi
    # Run the built DLL directly. Development env is required so static web assets (_framework/blazor.web.js) resolve.
    (cd "$ROOT/src/PlaneWeb.Web" && \
      ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$PORT" \
      ConnectionStrings__Default="Data Source=$DB" \
      PlaneWeb__Auth__AdminEmail="${ADMIN_EMAIL:-me@example.com}" PlaneWeb__Auth__AdminPassword="${ADMIN_PASSWORD:-dev-password-1}" \
      setsid nohup dotnet "$ROOT/src/PlaneWeb.Web/bin/${CONFIG:-Release}/net10.0/PlaneWeb.Web.dll" >"$LOG" 2>&1 </dev/null & echo $! >"$PIDFILE")
    for _ in $(seq 1 40); do
      curl -fs "http://localhost:$PORT/healthz" >/dev/null && { echo "up on :$PORT (pid $(cat "$PIDFILE"))"; exit 0; }
      sleep 1
    done
    echo "failed to start; see $LOG" >&2; exit 1 ;;
  stop)
    if running; then kill "$(cat "$PIDFILE")" && echo stopped; else echo "not running"; fi
    rm -f "$PIDFILE"
    # Also free the port in case an orphaned instance (e.g. from a manual run) is still listening.
    command -v fuser >/dev/null && fuser -k "$PORT/tcp" >/dev/null 2>&1 || true ;;
  status) running && echo "running (pid $(cat "$PIDFILE"))" || echo "not running" ;;
  log)    cat "$LOG" ;;
  *) echo "usage: $0 start|stop|status|log" >&2; exit 2 ;;
esac
