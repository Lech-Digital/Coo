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
GOOGLE_WEB_CLIENT_ID="${COO_GOOGLE_WEB_CLIENT_ID:-282211746968-l54aogbkec1gf1g0rp0n7bk2mhbg2c7r.apps.googleusercontent.com}"
APPLINKS_SHA256_DEBUG="${COO_APPLINKS_SHA256_DEBUG:-6E:7E:3C:4E:88:95:35:CE:FE:90:1C:8C:D2:C5:37:D1:E4:2C:FB:91:4D:B7:2D:BB:F5:7A:C8:BD:15:3A:28:7C}"
# Play App Signing's certificate (Play Console → Test and release → App signing), for invitation links in Play builds.
APPLINKS_SHA256_PLAY="${COO_APPLINKS_SHA256_PLAY:-8A:25:D0:C0:11:DC:FA:8A:56:0B:7C:52:96:B7:31:4A:C2:30:F4:5A:26:2E:EE:3C:13:2A:72:9F:04:91:7F:40}"
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

# ---- The folders and the secrets ----------------------------------------------------------------
# Generated on the server and written straight into .env (mode 600); never printed. Existing values are kept.
echo "==> Preparing $HOST:$REMOTE_DIR"
ssh "$HOST" "set -e; mkdir -p $REMOTE_DIR/deploy $REMOTE_DIR/backups $REMOTE_DIR/eng
  umask 077
  if [ ! -f $REMOTE_DIR/.env ]; then
    printf 'POSTGRES_PASSWORD=%s\n' \"\$(openssl rand -hex 32)\" > $REMOTE_DIR/.env
    echo '    Created .env with a new database password.'
  fi
  if ! grep -q '^AUTH_GOOGLE_WEB_CLIENT_ID=.' $REMOTE_DIR/.env; then
    sed -i '/^AUTH_GOOGLE_WEB_CLIENT_ID=/d' $REMOTE_DIR/.env
    printf 'AUTH_GOOGLE_WEB_CLIENT_ID=%s\n' '$GOOGLE_WEB_CLIENT_ID' >> $REMOTE_DIR/.env
    echo '    Added the Google Web client ID to .env.'
  fi
  if ! grep -q '^AUTH_SIGNING_KEY_K1=.' $REMOTE_DIR/.env; then
    sed -i '/^AUTH_SIGNING_KEY_K1=/d' $REMOTE_DIR/.env
    printf 'AUTH_SIGNING_KEY_K1=%s\n' \"\$(openssl rand -base64 32)\" >> $REMOTE_DIR/.env
    echo '    Added a new access-token signing key to .env.'
  fi
  if ! grep -q '^APPLINKS_SHA256_PLAY=.' $REMOTE_DIR/.env; then
    sed -i '/^APPLINKS_SHA256_PLAY=/d' $REMOTE_DIR/.env
    printf 'APPLINKS_SHA256_PLAY=%s\n' '$APPLINKS_SHA256_PLAY' >> $REMOTE_DIR/.env
    echo '    Added the Play signing fingerprint to .env.'
  fi
  if ! grep -q '^APPLINKS_SHA256_DEBUG=.' $REMOTE_DIR/.env; then
    sed -i '/^APPLINKS_SHA256_DEBUG=/d' $REMOTE_DIR/.env
    printf 'APPLINKS_SHA256_DEBUG=%s\n' '$APPLINKS_SHA256_DEBUG' >> $REMOTE_DIR/.env
    echo '    Added the App Link fingerprint to .env.'
  fi
  if ! grep -q '^INVITATIONS_CODE_PEPPER=.' $REMOTE_DIR/.env; then
    sed -i '/^INVITATIONS_CODE_PEPPER=/d' $REMOTE_DIR/.env
    printf 'INVITATIONS_CODE_PEPPER=%s\n' \"\$(openssl rand -base64 32)\" >> $REMOTE_DIR/.env
    echo '    Added a new invitation-code pepper to .env (open invitations stop working).'
  fi"

# ---- Sync -------------------------------------------------------------------------------------
echo "==> Syncing source ($SHA)"
rsync -rlptz --delete --exclude 'bin/' --exclude 'obj/' --exclude '.DS_Store' \
  "$ROOT/src/Shared" "$ROOT/src/Backend" "$HOST:$REMOTE_DIR/src/"
rsync -rlptz --delete "$ROOT/eng/banned-non-host" "$HOST:$REMOTE_DIR/eng/"
rsync -rlptz "$ROOT/Dockerfile" "$ROOT/.dockerignore" "$ROOT/NuGet.config" "$ROOT/Directory.Build.props" \
  "$ROOT/Directory.Packages.props" "$ROOT/BannedSymbols.txt" "$ROOT/.editorconfig" "$HOST:$REMOTE_DIR/"
rsync -rlptz "$ROOT/deploy/docker-compose.prod.yml" "$HOST:$REMOTE_DIR/deploy/"
# Copied as root without owners: hand everything to `deploy`, which GitHub's deploy (lechdigital-infra) logs in as.
ssh "$HOST" "id deploy >/dev/null 2>&1 && find $REMOTE_DIR -path $REMOTE_DIR/backups -prune -o ! -user deploy -print0 | xargs -0 -r chown deploy:deploy || true"

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
