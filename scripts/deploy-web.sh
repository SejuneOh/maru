#!/usr/bin/env bash
# Builds the static web with production values and deploys it to Cloudflare Pages.
# Usage: scripts/deploy-web.sh [web-env-file] [pages-project]
#   web-env-file  KEY=VALUE lines with NEXT_PUBLIC_* values (default ~/.config/maru/web.env)
#   pages-project Pages project name (default maru-board)
# web/.env.local is moved aside during the build: its values end up in the bundle even when the
# same variables are set in the environment.
set -euo pipefail

ENV_FILE="${1:-$HOME/.config/maru/web.env}"
PROJECT="${2:-maru-board}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WEB="$ROOT/web"

[ -f "$ENV_FILE" ] || { echo "env file not found: $ENV_FILE" >&2; exit 1; }
value() { { grep -E "^$1=" "$ENV_FILE" || true; } | head -1 | cut -d= -f2- | tr -d '\r'; }
SUPABASE_URL="$(value NEXT_PUBLIC_SUPABASE_URL)"
API_URL="$(value NEXT_PUBLIC_API_URL)"
[ -n "$SUPABASE_URL" ] && [ -n "$API_URL" ] && [ -n "$(value NEXT_PUBLIC_SUPABASE_ANON_KEY)" ] \
  || { echo "NEXT_PUBLIC_SUPABASE_URL, NEXT_PUBLIC_SUPABASE_ANON_KEY and NEXT_PUBLIC_API_URL are required" >&2; exit 1; }

cd "$WEB"
if [ -e .env.local.deploy-hold ]; then
  echo "web/.env.local.deploy-hold exists from an earlier run; restore or remove it first" >&2
  exit 1
fi
if [ -e .env.local ]; then
  mv .env.local .env.local.deploy-hold
  trap 'mv .env.local.deploy-hold .env.local' EXIT
fi

pnpm install --frozen-lockfile
rm -rf .next out
"$ROOT/scripts/with-env.py" "$ENV_FILE" pnpm build

bundle_has() { grep -rqF "$1" out/_next/static; }
bundle_has "${SUPABASE_URL#https://}" || { echo "build does not contain $SUPABASE_URL" >&2; exit 1; }
bundle_has "${API_URL#https://}" || { echo "build does not contain $API_URL" >&2; exit 1; }
if grep -rqE 'localhost:5080' out/_next/static; then echo "build still points at localhost:5080" >&2; exit 1; fi

COMMIT="$(git -C "$ROOT" rev-parse HEAD)"
npx -y wrangler@4 pages deploy out --project-name "$PROJECT" --branch main \
  --commit-hash "$COMMIT" --commit-message "$(git -C "$ROOT" log -1 --format=%s)" --commit-dirty=true
