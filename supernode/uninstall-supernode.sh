#!/usr/bin/env bash
# Remove this deployment's service units; keep binaries and source archives.
set -euo pipefail
SERVICE_NAME=mikun2n-supernode
CONF_DIR=/etc/n3n
PURGE=0
while [ $# -gt 0 ]; do
    case "$1" in
        --purge) PURGE=1; shift ;;
        --service-name)
            [ $# -ge 2 ] || { echo "Missing service name" >&2; exit 1; }
            SERVICE_NAME="$2"; shift 2 ;;
        -h|--help)
            echo "Usage: sudo bash $0 [--service-name NAME] [--purge]"
            echo "--purge also deletes configuration. Binaries are retained."
            exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done
[[ "$SERVICE_NAME" =~ ^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$ ]] || { echo "Invalid service name" >&2; exit 1; }
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo" >&2; exit 1; }
for unit in "${SERVICE_NAME}.service" "${SERVICE_NAME}-probe.service"; do
    if [ -f "/etc/systemd/system/$unit" ]; then
        systemctl disable --now "$unit"
    fi
done
rm -f "/etc/systemd/system/${SERVICE_NAME}.service" "/etc/systemd/system/${SERVICE_NAME}-probe.service"
systemctl daemon-reload
if [ "$PURGE" -eq 1 ]; then
    rm -f "${CONF_DIR}/${SERVICE_NAME}.conf" "${CONF_DIR}/${SERVICE_NAME}.env" "${CONF_DIR}/${SERVICE_NAME}-community.list"
fi
echo "Service units removed. Binaries and source archives have been retained."
