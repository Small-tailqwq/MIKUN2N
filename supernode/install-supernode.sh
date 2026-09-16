#!/usr/bin/env bash
# Install the patched supernode and its same-host NAT observation service.
set -euo pipefail
SERVICE_NAME=mikun2n-supernode
BINARY=""
PORT=3076
COMMUNITY=""
CIDR=""
PREFIX=/opt/mikun2n/n3n
CONF_DIR=/etc/n3n
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
usage() {
    echo "Usage: sudo bash $0 --binary /absolute/path/n3n-supernode [--port 3076]"
    echo "       [--community mygroup --cidr 10.42.0.0/24] [--service-name NAME] [--prefix PATH]"
    echo "Existing configuration is preserved. Edit it directly before restarting the service."
}
while [ $# -gt 0 ]; do
    case "$1" in
        -h|--help) usage; exit 0 ;;
        --binary|--port|--community|--cidr|--prefix|--service-name)
            [ $# -ge 2 ] && [ -n "$2" ] || { echo "Missing value for $1" >&2; exit 1; }
            case "$1" in
                --binary) BINARY="$2" ;;
                --port) PORT="$2" ;;
                --community) COMMUNITY="$2" ;;
                --cidr) CIDR="$2" ;;
                --prefix) PREFIX="$2" ;;
                --service-name) SERVICE_NAME="$2" ;;
            esac
            shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done
[[ "$SERVICE_NAME" =~ ^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$ ]] || { echo "Invalid service name" >&2; exit 1; }
[[ "$PREFIX" =~ ^/[a-zA-Z0-9_./+-]+$ ]] || { echo "Use an absolute prefix without spaces or special characters" >&2; exit 1; }
[[ "$PORT" =~ ^[0-9]{1,5}$ ]] && ((10#$PORT >= 1 && 10#$PORT <= 65535)) || { echo "Port must be 1-65535" >&2; exit 1; }
PORT=$((10#$PORT))
[[ "$PORT" != 21001 && "$PORT" != 21002 ]] || { echo "Ports 21001 and 21002 are reserved for NAT observation" >&2; exit 1; }
if [ -n "$CIDR" ] && [ -z "$COMMUNITY" ]; then
    echo "--cidr requires --community" >&2; exit 1
fi
if [ -n "$COMMUNITY" ]; then
    [[ "$COMMUNITY" != *[[:space:]]* ]] && [ "$(printf '%s' "$COMMUNITY" | wc -c)" -le 20 ] || {
        echo "Community must be at most 20 UTF-8 bytes without whitespace" >&2; exit 1;
    }
fi
BINARY="${BINARY:-${PREFIX}/sbin/n3n-supernode}"
[[ "$BINARY" =~ ^/[a-zA-Z0-9_./+-]+$ ]] && [ -x "$BINARY" ] || { echo "Specify an executable with an absolute --binary path" >&2; exit 1; }
command -v systemctl >/dev/null || { echo "This installer requires systemd" >&2; exit 1; }
[ -x /usr/bin/python3 ] || { echo "Install Python 3 first" >&2; exit 1; }
if [ -n "$CIDR" ]; then
    /usr/bin/python3 -c 'import ipaddress,sys; ipaddress.IPv4Network(sys.argv[1], strict=True)' "$CIDR"
fi
PROBE_SOURCE="$SCRIPT_DIR/../tools/natpunch/natpunch-server.py"
for required in "$SCRIPT_DIR/mikun2n-supernode.service" "$SCRIPT_DIR/mikun2n-probe.service" "$SCRIPT_DIR/supernode.conf.template" "$PROBE_SOURCE"; do
    [ -f "$required" ] || { echo "Missing release file: $required" >&2; exit 1; }
done
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo" >&2; exit 1; }
CONF_FILE="${CONF_DIR}/${SERVICE_NAME}.conf"
COMMUNITY_FILE="${CONF_DIR}/${SERVICE_NAME}-community.list"
PROBE_FILE="${PREFIX}/lib/natpunch-server.py"
INSTALLED_BINARY="${PREFIX}/sbin/n3n-supernode"
install -d -m 0755 "$CONF_DIR" "${PREFIX}/lib" "${PREFIX}/sbin"
if [ "$(readlink -f "$BINARY")" != "$(readlink -f "$INSTALLED_BINARY")" ]; then
    install -m 0755 "$BINARY" "$INSTALLED_BINARY"
fi
install -m 0644 "$PROBE_SOURCE" "$PROBE_FILE"
if [ ! -e "$CONF_FILE" ]; then
    COMMUNITY_LINE="#community_file=${COMMUNITY_FILE}"
    if [ -n "$COMMUNITY" ]; then
        if [ ! -e "$COMMUNITY_FILE" ]; then
            printf '%s%s\n' "$COMMUNITY" "${CIDR:+ $CIDR}" > "$COMMUNITY_FILE"
            chmod 0644 "$COMMUNITY_FILE"
        fi
        COMMUNITY_LINE="community_file=${COMMUNITY_FILE}"
    fi
    sed -e "s|__BIND_PORT__|${PORT}|g" \
        -e "s|^#community_file=.*|${COMMUNITY_LINE}|" \
        "$SCRIPT_DIR/supernode.conf.template" > "$CONF_FILE"
    chmod 0644 "$CONF_FILE"
else
    echo "Preserved existing configuration: $CONF_FILE"
fi
if [ ! -e "${CONF_DIR}/${SERVICE_NAME}.env" ]; then
    printf '%s\n' '# Optional federation name shared by the member supernodes.' '#N3N_FEDERATION=myfederation' > "${CONF_DIR}/${SERVICE_NAME}.env"
    chmod 0600 "${CONF_DIR}/${SERVICE_NAME}.env"
fi
sed -e "s|__BINARY__|${INSTALLED_BINARY}|g" -e "s|__SERVICE_NAME__|${SERVICE_NAME}|g" \
    "$SCRIPT_DIR/mikun2n-supernode.service" > "/etc/systemd/system/${SERVICE_NAME}.service"
sed -e "s|__PROBE_FILE__|${PROBE_FILE}|g" -e "s|__SERVICE_NAME__|${SERVICE_NAME}|g" \
    "$SCRIPT_DIR/mikun2n-probe.service" > "/etc/systemd/system/${SERVICE_NAME}-probe.service"
chmod 0644 "/etc/systemd/system/${SERVICE_NAME}.service" "/etc/systemd/system/${SERVICE_NAME}-probe.service"
systemctl daemon-reload
systemctl enable "${SERVICE_NAME}.service" "${SERVICE_NAME}-probe.service"
systemctl restart "${SERVICE_NAME}-probe.service" "${SERVICE_NAME}.service"
systemctl --no-pager --full status "${SERVICE_NAME}.service" "${SERVICE_NAME}-probe.service"
echo "Allow the configured supernode UDP port and UDP 21001/21002 in both the host firewall and cloud security group."
