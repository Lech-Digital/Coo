#!/usr/bin/env bash
#
# restore-drill.sh: prove the newest nightly backup restores (Spec 008 §6). A backup that was never restored is not
# trusted.
#
# It restores the newest /opt/coo/backups/coo-*.dump into a scratch database (coo_restore_drill) on coo-postgres,
# compares its row counts with the live database, then drops the scratch database. The live database is only read.
#
# Usage:
#   ./deploy/restore-drill.sh            # the newest nightly dump, on the Hetzner host, through ssh
#   ./deploy/restore-drill.sh --fresh    # take a dump now (as coo-backup does), then restore that one
#   COO_DRILL_LOCAL=<container> PGHOST=localhost ./deploy/restore-drill.sh   # against a local test container
set -euo pipefail

HOST="${COO_HOST:-coldstart-prod}"
FRESH=0
[ "${1:-}" = "--fresh" ] && FRESH=1

read -r -d '' DRILL <<'BODY' || true
set -euo pipefail
PGHOST="${PGHOST:-coo-postgres}"
export PGHOST
if [ "${FRESH:-0}" = 1 ]; then
  ts=$(date -u +%F-%H%M)
  pg_dump -U coo -d coo -Fc -f "/backups/coo-$ts.dump"
  echo "Fresh dump: coo-$ts.dump"
fi
latest=$(ls -1t /backups/coo-*.dump 2>/dev/null | head -1 || true)
if [ -z "$latest" ]; then
  echo "DRILL FAILED: no dump in /backups yet." >&2
  exit 1
fi
echo "Restoring $(basename "$latest") into coo_restore_drill"
psql -U coo -d coo -q -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS coo_restore_drill;" -c "CREATE DATABASE coo_restore_drill;"
trap 'psql -U coo -d coo -q -c "DROP DATABASE IF EXISTS coo_restore_drill;" >/dev/null 2>&1 || true' EXIT
pg_restore -U coo -d coo_restore_drill --no-owner --exit-on-error "$latest"

status=0
printf '%-22s %10s %10s\n' TABLE LIVE RESTORED
for table in households entry_kind_versions care_entries; do
  live=$(psql -U coo -d coo -tAc "SELECT count(*) FROM $table;")
  restored=$(psql -U coo -d coo_restore_drill -tAc "SELECT count(*) FROM $table;")
  printf '%-22s %10s %10s\n' "$table" "$live" "$restored"
  # Rows written after the dump are fine; rows that vanished are not. The kind catalogue never shrinks.
  if [ "$restored" -gt "$live" ] || { [ "$table" = entry_kind_versions ] && [ "$restored" -ne "$live" ]; }; then
    status=1
  fi
done
if [ "$status" -eq 0 ]; then
  echo "DRILL OK: the backup restores, and its counts are consistent with the live database."
else
  echo "DRILL FAILED: the restored counts do not match." >&2
fi
exit "$status"
BODY

if [ -n "${COO_DRILL_LOCAL:-}" ]; then
  # The container's own PGPASSWORD is kept unless one is given here.
  env_args=(-e PGHOST="${PGHOST:-localhost}")
  [ -n "${PGPASSWORD:-}" ] && env_args+=(-e PGPASSWORD="$PGPASSWORD")
  docker exec -i "${env_args[@]}" -e FRESH="$FRESH" "$COO_DRILL_LOCAL" bash -s <<< "$DRILL"
else
  ssh "$HOST" "docker exec -i -e FRESH=$FRESH coo-backup bash -s" <<< "$DRILL"
fi
