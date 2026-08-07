#!/usr/bin/env bash
#
# Installs the DICOM Migration Server as a systemd unit.
#
# Run as root on the target Linux server, from the unpacked package directory
# (the one containing app/, install.sh, uninstall.sh, dicom-migration.service):
#
#   sudo ./install.sh --source-host 10.0.0.5 --source-ae SOURCE_PACS \
#                     --dest-host 10.0.0.9  --dest-ae DEST_PACS
#
# Re-running is safe: it upgrades the binaries in place and preserves data/ (the
# SQLite inventory) and logs/. Settings are re-seeded from the flags you pass, so
# pass the same AE config on an upgrade, or omit --reseed-settings to keep what
# the dashboard currently has.

set -euo pipefail

INSTALL_DIR="/opt/dicom-migration"
SERVICE_NAME="dicom-migration"
SERVICE_USER="dicommigration"

PORT=5080
# Bind loopback by default: the dashboard has no authentication and its endpoints can
# start a migration against live PACS. Use --listen 0.0.0.0 only behind a trusted
# network or a fronting proxy that adds auth; otherwise reach it over an SSH tunnel.
LISTEN="127.0.0.1"

LOCAL_AE="MIGRATE"
SOURCE_AE="SOURCE_PACS"
SOURCE_HOST="127.0.0.1"
SOURCE_PORT=104
DEST_AE="DEST_PACS"
DEST_HOST="127.0.0.1"
DEST_PORT=104

MAX_PARALLEL_MOVES=1
DELAY_BETWEEN_MOVES_MS=0
MOVE_TIMEOUT_MINUTES=120
MAX_ATTEMPTS=2
VERIFY_LEVEL="Study"

step() { printf '[install] %s\n' "$1"; }
fail() { printf '[install] ERROR: %s\n' "$1" >&2; exit 1; }

usage() {
    sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'
    cat <<'EOF'

Options (all optional; defaults in brackets):
  --install-dir DIR      install location [/opt/dicom-migration]
  --service-name NAME    systemd unit name [dicom-migration]
  --service-user USER    system account to run as [dicommigration]
  --port N               dashboard port [5080]
  --listen ADDR          dashboard bind address [127.0.0.1]
  --local-ae AE          this server's AE title [MIGRATE]
  --source-ae AE         --source-host HOST   --source-port N   [SOURCE_PACS 127.0.0.1 104]
  --dest-ae AE           --dest-host HOST     --dest-port N     [DEST_PACS 127.0.0.1 104]
  --max-parallel-moves N [1]
  --delay-ms N           delay between moves [0]
  --move-timeout-min N   [120]
  --max-attempts N       [2]
  --verify-level LEVEL   Study or Series [Study]
  --keep-settings        do not overwrite existing DB settings on upgrade
  -h, --help             show this help
EOF
}

KEEP_SETTINGS=0

while [ $# -gt 0 ]; do
    case "$1" in
        --install-dir)         INSTALL_DIR="$2"; shift 2 ;;
        --service-name)        SERVICE_NAME="$2"; shift 2 ;;
        --service-user)        SERVICE_USER="$2"; shift 2 ;;
        --port)                PORT="$2"; shift 2 ;;
        --listen)              LISTEN="$2"; shift 2 ;;
        --local-ae)            LOCAL_AE="$2"; shift 2 ;;
        --source-ae)           SOURCE_AE="$2"; shift 2 ;;
        --source-host)         SOURCE_HOST="$2"; shift 2 ;;
        --source-port)         SOURCE_PORT="$2"; shift 2 ;;
        --dest-ae)             DEST_AE="$2"; shift 2 ;;
        --dest-host)           DEST_HOST="$2"; shift 2 ;;
        --dest-port)           DEST_PORT="$2"; shift 2 ;;
        --max-parallel-moves)  MAX_PARALLEL_MOVES="$2"; shift 2 ;;
        --delay-ms)            DELAY_BETWEEN_MOVES_MS="$2"; shift 2 ;;
        --move-timeout-min)    MOVE_TIMEOUT_MINUTES="$2"; shift 2 ;;
        --max-attempts)        MAX_ATTEMPTS="$2"; shift 2 ;;
        --verify-level)        VERIFY_LEVEL="$2"; shift 2 ;;
        --keep-settings)       KEEP_SETTINGS=1; shift ;;
        -h|--help)             usage; exit 0 ;;
        *) fail "Unknown option: $1 (use --help)" ;;
    esac
done

[ "$(id -u)" -eq 0 ] || fail "Must run as root (use sudo)."
command -v systemctl >/dev/null 2>&1 || fail "systemctl not found; this installer targets systemd."

script_dir="$(cd "$(dirname "$0")" && pwd)"
payload="$script_dir/app"
unit_template="$script_dir/dicom-migration.service"
exe="$INSTALL_DIR/DicomMigration"

[ -f "$payload/DicomMigration" ] || fail "No published payload at $payload (expected app/DicomMigration). Build it with installer/linux/build-tarball.ps1 on a machine with the .NET SDK."
[ -f "$unit_template" ] || fail "Missing unit template: $unit_template"

# --- 1. Service account ---------------------------------------------------------------
if id -u "$SERVICE_USER" >/dev/null 2>&1; then
    step "Service account $SERVICE_USER already exists."
else
    step "Creating system account $SERVICE_USER ..."
    useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

# --- 2. Stop an existing unit before replacing binaries --------------------------------
if systemctl list-unit-files "$SERVICE_NAME.service" >/dev/null 2>&1 \
   && systemctl is-active --quiet "$SERVICE_NAME"; then
    step "Stopping running $SERVICE_NAME ..."
    systemctl stop "$SERVICE_NAME"
fi

# --- 3. Stage binaries -----------------------------------------------------------------
step "Installing published files into $INSTALL_DIR ..."
mkdir -p "$INSTALL_DIR"
# Copy contents without deleting an existing data/ or logs/ (mirrors the Windows install).
cp -a "$payload/." "$INSTALL_DIR/"
chmod +x "$exe"
mkdir -p "$INSTALL_DIR/data" "$INSTALL_DIR/logs"

# --- 4. appsettings.Production.json (UTF-8, no BOM) ------------------------------------
step "Writing appsettings.Production.json ..."
cat > "$INSTALL_DIR/appsettings.Production.json" <<EOF
{
  "Urls": "http://$LISTEN:$PORT",
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": { "Microsoft.AspNetCore": "Warning" }
    }
  },
  "Dicom": {
    "LocalAeTitle": "$LOCAL_AE",
    "Source": { "AeTitle": "$SOURCE_AE", "Host": "$SOURCE_HOST", "Port": $SOURCE_PORT },
    "Destination": { "AeTitle": "$DEST_AE", "Host": "$DEST_HOST", "Port": $DEST_PORT },
    "Move": {
      "MaxParallelMoves": $MAX_PARALLEL_MOVES,
      "DelayBetweenMovesMs": $DELAY_BETWEEN_MOVES_MS,
      "MoveTimeoutMinutes": $MOVE_TIMEOUT_MINUTES,
      "MaxAttempts": $MAX_ATTEMPTS,
      "AssociationTimeoutSeconds": 60
    },
    "Verify": { "Level": "$VERIFY_LEVEL" },
    "Inventory": { "FetchSeriesCounts": false }
  },
  "Database": { "Path": "data/migration.db" }
}
EOF

# --- 5. Database schema + settings seed -------------------------------------------------
if [ "$KEEP_SETTINGS" -eq 1 ] && [ -f "$INSTALL_DIR/data/migration.db" ]; then
    step "Keeping existing database settings (--keep-settings)."
else
    step "Initializing database and seeding settings (init-db) ..."
    DOTNET_ENVIRONMENT=Production DOTNET_EnableDiagnostics=0 "$exe" init-db
fi

# Ownership after init-db so root-created files end up owned by the service account.
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"

# --- 6. systemd unit --------------------------------------------------------------------
step "Installing systemd unit $SERVICE_NAME.service ..."
sed -e "s#@INSTALL_DIR@#$INSTALL_DIR#g" \
    -e "s#@SERVICE_USER@#$SERVICE_USER#g" \
    "$unit_template" > "/etc/systemd/system/$SERVICE_NAME.service"
chmod 0644 "/etc/systemd/system/$SERVICE_NAME.service"

systemctl daemon-reload
step "Enabling and starting $SERVICE_NAME ..."
systemctl enable --quiet "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"

# Type=notify means systemctl returns once the app signalled readiness, so this status
# reflects a host that actually started.
echo
if systemctl is-active --quiet "$SERVICE_NAME"; then
    step "Done. $SERVICE_NAME is active."
else
    step "Unit did not reach active state. Recent log:"
    journalctl -u "$SERVICE_NAME" -n 40 --no-pager || true
    exit 1
fi

step "Dashboard:  http://$LISTEN:$PORT/"
if [ "$LISTEN" = "127.0.0.1" ]; then
    step "  (loopback only - reach it with: ssh -L $PORT:localhost:$PORT <this-host>)"
fi
step "Database:   $INSTALL_DIR/data/migration.db"
step "Logs:       journalctl -u $SERVICE_NAME -f   (and $INSTALL_DIR/logs/)"
step "Remember to register AE '$LOCAL_AE' plus this host's IP on the source PACS for C-FIND and C-MOVE,"
step "and register destination AE '$DEST_AE' there as a move destination."
