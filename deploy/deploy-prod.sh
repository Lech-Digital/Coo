#!/usr/bin/env bash
#
# deploy-prod.sh: ship the Coo backend to the Hetzner host (Spec 008 §7), the way PurePrep and CoreChoice ship.
#
# The server holds a copy of the source, not a git checkout. This syncs exactly what the image is built from,
# then rebuilds the containers in place. Coldstart's Caddy handles TLS and routing; the Caddy block is a one-time
# step in DEPLOY.md, not part of this script.
#
# Usage:
#   ./deploy/deploy-prod.sh --confirm
#
set -euo pipefail

HOST="${COO_HOST:-coldstart-prod}"
PUBLIC_URL="${COO_PUBLIC_URL:-https://api.coo.lechdigital.nl}"
REMOTE_DIR="${COO_REMOTE_DIR:-/opt/coo}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ "${1:-}" != "--confirm" ]; then
  echo "This deploys to production ($HOST:$REMOTE_DIR)." >&2
  echo "Re-run with --confirm to proceed." >&2
  exit 2
fi

# ---- Interlock 1: what runs is exactly a commit ------------------------------------------------
# /health reports the commit; that is only true if nothing uncommitted is shipped with it.
if [ -n "$(git -C "$ROOT" status --porcelain -- src/Backend src/Shared Dockerfile .dockerignore deploy Directory.Build.props Directory.Packages.props)" ]; then
  echo "REFUSING TO DEPLOY: the backend has uncommitted changes. Commit them first." >&2
  exit 1
fi
SHA="$(git -C "$ROOT" rev-parse --short HEAD)"

# ---- Interlock 2: every test passes, including the PostgreSQL ones (they need Docker Desktop) ---
echo "==> Running tests"
dotnet test "$ROOT/Coo.Tests.slnf" -c Release --nologo -v q >/dev/null || {
  echo "REFUSING TO DEPLOY: tests are failing (or Docker is not running for the PostgreSQL tests)." >&2; exit 1; }
echo "    Tests green."

# ---- First deploy only: the folders and the secret --------------------------------------------
# The password is generated on the server and written straight into .env (mode 600). It is never printed.
echo "==> Preparing $HOST:$REMOTE_DIR"
ssh "$HOST" "set -e; mkdir -p $REMOTE_DIR/deploy $REMOTE_DIR/backups $REMOTE_DIR/eng
  if [ ! -f $REMOTE_DIR/.env ]; then
    umask 077
    printf 'POSTGRES_PASSWORD=%s\n' \"\$(openssl rand -hex 32)\" > $REMOTE_DIR/.env
    echo '    Created .env with a new database password.'
  fi"

# ---- Sync -------------------------------------------------------------------------------------
echo "==> Syncing source ($SHA)"
rsync -az --delete --exclude 'bin/' --exclude 'obj/' --exclude '.DS_Store' \
  "$ROOT/src/Shared" "$ROOT/src/Backend" "$HOST:$REMOTE_DIR/src/"
rsync -az --delete "$ROOT/eng/banned-non-host" "$HOST:$REMOTE_DIR/eng/"
rsync -az "$ROOT/Dockerfile" "$ROOT/.dockerignore" "$ROOT/NuGet.config" "$ROOT/Directory.Build.props" \
  "$ROOT/Directory.Packages.props" "$ROOT/BannedSymbols.txt" "$ROOT/.editorconfig" "$HOST:$REMOTE_DIR/"
rsync -az "$ROOT/deploy/docker-compose.prod.yml" "$HOST:$REMOTE_DIR/deploy/"

# ---- Build & restart --------------------------------------------------------------------------
echo "==> Rebuilding containers"
ssh "$HOST" "cd $REMOTE_DIR && GIT_SHA=$SHA docker compose --env-file .env -f deploy/docker-compose.prod.yml up -d --build"

# ---- Verify -----------------------------------------------------------------------------------
# coo-api publishes no port, so health is checked through the public URL. That also proves Caddy routes to this
# container, and the commit proves it is the new one.
echo "==> Waiting for health"
for _ in $(seq 1 45); do
  sleep 2
  body="$(curl -sf -m 10 "$PUBLIC_URL/health" 2>/dev/null || true)"
  if [ -n "$body" ] && grep -q "+$SHA" <<< "$body"; then
    echo "    Healthy: $body"
    exit 0
  fi
done

echo >&2
echo "DEPLOY FAILED: $PUBLIC_URL/health did not report $SHA. Last answer: ${body:-none}" >&2
ssh "$HOST" "cd $REMOTE_DIR && docker compose --env-file .env -f deploy/docker-compose.prod.yml logs --tail 30 coo-api" >&2
exit 1
