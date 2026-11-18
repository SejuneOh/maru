#!/usr/bin/env bash
# Opens a quick tunnel to the local API and points the work-board-api Worker at it.
# Usage: scripts/api-tunnel.sh [local-api-url]   (default http://127.0.0.1:5080)
# Needs cloudflared and a one-time `npx wrangler@4 login`. Runs until cloudflared exits.
set -euo pipefail

LOCAL_API="${1:-http://127.0.0.1:5080}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOG="$(mktemp -t api-tunnel.XXXXXX.log)"

cloudflared tunnel --no-autoupdate --url "$LOCAL_API" >"$LOG" 2>&1 &
TUNNEL_PID=$!
trap 'kill "$TUNNEL_PID" 2>/dev/null || true; rm -f "$LOG"' EXIT
trap 'exit 130' INT TERM

# cloudflared prints the new address inside a box line ("|  https://<words>.trycloudflare.com  |"); errors mention
# https://api.trycloudflare.com, which must never be taken as the tunnel.
URL=""
for _ in $(seq 1 60); do
  URL="$(grep -E '^[^|]*\|[[:space:]]+https://' "$LOG" | grep -oE 'https://[a-z0-9]+(-[a-z0-9]+)+\.trycloudflare\.com' | head -1 || true)"
  [ -n "$URL" ] && break
  kill -0 "$TUNNEL_PID" 2>/dev/null || { cat "$LOG" >&2; exit 1; }
  sleep 1
done
[ -n "$URL" ] || { echo "tunnel address not found" >&2; cat "$LOG" >&2; exit 1; }

echo "tunnel: $URL"
printf '%s' "$URL" | npx -y wrangler@4 secret put UPSTREAM --config "$ROOT/deploy/api-proxy/wrangler.toml"
echo "worker now forwards to $URL"

wait "$TUNNEL_PID"
