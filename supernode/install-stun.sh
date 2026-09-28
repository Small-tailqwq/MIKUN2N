#!/usr/bin/env bash
# Install the optional IPv6 mapping observer for edges behind NAT66. No supernode
# configuration is changed; the observer only reads the supernode's edge list.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IPV6=""
MGMT="/run/n3n/mikun2n-supernode/mgmt"
while [ $# -gt 0 ]; do
    case "$1" in
        --ipv6|--mgmt)
            [ $# -ge 2 ] || { echo "Missing value for $1" >&2; exit 1; }
            case "$1" in --ipv6) IPV6="$2" ;; --mgmt) MGMT="$2" ;; esac
            shift 2 ;;
        *) echo "Usage: bash $0 [--ipv6 GLOBAL_IPV6] [--mgmt SOCKET]" >&2; exit 1 ;;
    esac
done
[ "$EUID" -eq 0 ] || { echo "Run as root" >&2; exit 1; }
for tool in python3 systemctl ip; do command -v "$tool" >/dev/null; done
if [ -z "$IPV6" ]; then
    IPV6=$(ip -6 -o addr show scope global 2>/dev/null | grep -v -e temporary -e deprecated |
        awk '{print $4}' | cut -d/ -f1 | head -n1)
fi
python3 -c 'import ipaddress,sys; a=ipaddress.IPv6Address(sys.argv[1]); sys.exit(0 if a.is_global else 1)' "$IPV6" ||
    { echo "No global IPv6 address found; pass --ipv6" >&2; exit 1; }
[ -S "$MGMT" ] || { echo "Supernode management socket $MGMT not found" >&2; exit 1; }

install -d -m 0755 /etc/mikun2n-stun /opt/mikun2n/stun
printf 'MIKUN2N_STUN_IPV6=%s\nMIKUN2N_STUN_MGMT=%s\n' "$IPV6" "$MGMT" > /etc/mikun2n-stun/stun.env
chmod 0644 /etc/mikun2n-stun/stun.env
install -m 0755 "$SCRIPT_DIR/mikun2n-stun.py" /opt/mikun2n/stun/
install -m 0644 "$SCRIPT_DIR/mikun2n-stun.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable mikun2n-stun.service
systemctl restart mikun2n-stun.service
systemctl is-active --quiet mikun2n-stun.service
if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
    ufw allow 3478/udp comment 'MikuN2N IPv6 mapping observer' >/dev/null
fi
echo "Installed the IPv6 mapping observer on UDP 3478 (advertising $IPV6)."
echo "Allow UDP 3478 for both IPv4 and IPv6 in the provider security group."
