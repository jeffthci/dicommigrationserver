#!/usr/bin/env bash
#
# Removes the DICOM Migration Server systemd unit.
#
#   sudo ./uninstall.sh                 # stop + remove unit, keep files and data
#   sudo ./uninstall.sh --purge         # also delete the install dir INCLUDING the inventory DB
#   sudo ./uninstall.sh --purge --remove-user
#
# Without --purge the SQLite inventory in <install-dir>/data survives, so a reinstall
# resumes rather than re-inventorying the source PACS.

set -euo pipefail

INSTALL_DIR="/opt/dicom-migration"
SERVICE_NAME="dicom-migration"
SERVICE_USER="dicommigration"
PURGE=0
REMOVE_USER=0

step() { printf '[uninstall] %s\n' "$1"; }
fail() { printf '[uninstall] ERROR: %s\n' "$1" >&2; exit 1; }

while [ $# -gt 0 ]; do
    case "$1" in
        --install-dir)  INSTALL_DIR="$2"; shift 2 ;;
        --service-name) SERVICE_NAME="$2"; shift 2 ;;
        --service-user) SERVICE_USER="$2"; shift 2 ;;
        --purge)        PURGE=1; shift ;;
        --remove-user)  REMOVE_USER=1; shift ;;
        -h|--help)      sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) fail "Unknown option: $1 (use --help)" ;;
    esac
done

[ "$(id -u)" -eq 0 ] || fail "Must run as root (use sudo)."

unit_path="/etc/systemd/system/$SERVICE_NAME.service"

if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    step "Stopping $SERVICE_NAME ..."
    systemctl stop "$SERVICE_NAME"
fi

if [ -f "$unit_path" ]; then
    step "Disabling and removing $unit_path ..."
    systemctl disable --quiet "$SERVICE_NAME" 2>/dev/null || true
    rm -f "$unit_path"
    systemctl daemon-reload
    systemctl reset-failed "$SERVICE_NAME" 2>/dev/null || true
else
    step "No unit file at $unit_path; nothing to disable."
fi

if [ "$PURGE" -eq 1 ]; then
    if [ -d "$INSTALL_DIR" ]; then
        step "Deleting $INSTALL_DIR (including the inventory database) ..."
        rm -rf "$INSTALL_DIR"
    fi
else
    step "Left $INSTALL_DIR in place (use --purge to delete it and the inventory database)."
fi

if [ "$REMOVE_USER" -eq 1 ] && id -u "$SERVICE_USER" >/dev/null 2>&1; then
    step "Removing service account $SERVICE_USER ..."
    userdel "$SERVICE_USER" || true
fi

step "Done."
