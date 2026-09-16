#!/usr/bin/env bash
# Install only the optional HTTPS test-log receiver. No supernode configuration is changed.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST=""
BATCH=""
while [ $# -gt 0 ]; do
    case "$1" in
        --host|--batch)
            [ $# -ge 2 ] || { echo "Missing value for $1" >&2; exit 1; }
            case "$1" in --host) HOST="$2" ;; --batch) BATCH="$2" ;; esac
            shift 2 ;;
        *) echo "Usage: bash $0 --host HOST --batch BATCH" >&2; exit 1 ;;
    esac
done
[ "$EUID" -eq 0 ] || { echo "Run as root" >&2; exit 1; }
[[ "$HOST" =~ ^[a-zA-Z0-9.-]+$ ]] || { echo "Supply an IPv4 address or DNS hostname" >&2; exit 1; }
[[ "$BATCH" =~ ^[a-zA-Z0-9-]{1,64}$ ]] || { echo "Invalid batch id" >&2; exit 1; }
for tool in python3 openssl systemctl; do command -v "$tool" >/dev/null; done
if ! id mikun2n-diag >/dev/null 2>&1; then
    useradd --system --user-group --no-create-home --shell /usr/sbin/nologin mikun2n-diag
fi
install -d -m 0750 -o root -g mikun2n-diag /etc/mikun2n-diagnostics
install -d -m 0755 /opt/mikun2n/diagnostics
python3 - "$HOST" "$BATCH" <<'PY'
import hashlib, ipaddress, json, os, pathlib, secrets, ssl, subprocess, sys
host, batch = sys.argv[1:]
root = pathlib.Path('/etc/mikun2n-diagnostics')
os.umask(0o077)
profile = root / 'client.json'
if profile.exists():
    old = json.loads(profile.read_text())
    if old['BatchId'] != batch or old['UploadUrl'] != f'https://{host}:5443/':
        raise SystemExit('Existing receiver belongs to another batch/host; preserve it and select a new deployment explicitly.')
    old['RetentionDays'] = 1
    profile.write_text(json.dumps(old), encoding='utf-8')
else:
    try:
        ipaddress.ip_address(host)
        san = 'IP:' + host
    except ValueError:
        san = 'DNS:' + host
    subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:3072', '-nodes', '-days', '90',
                    '-subj', '/CN=MikuN2N test diagnostics', '-addext', 'subjectAltName=' + san,
                    '-keyout', str(root/'key.pem'), '-out', str(root/'cert.pem')], check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    token = secrets.token_hex(32)
    cert = ssl.PEM_cert_to_DER_cert((root/'cert.pem').read_text())
    profile.write_text(json.dumps({'BatchId': batch, 'UploadUrl': f'https://{host}:5443/',
                                   'CertificateSha256': hashlib.sha256(cert).hexdigest(),
                                   'UploadToken': token, 'RetentionDays': 1}), encoding='utf-8')
    (root/'receiver.env').write_text('MIKUN2N_DIAGNOSTICS_BATCH=' + batch + '\n' +
        'MIKUN2N_DIAGNOSTICS_TOKEN_SHA256=' + hashlib.sha256(token.encode()).hexdigest() + '\n')
PY
chown root:mikun2n-diag /etc/mikun2n-diagnostics/{cert,key}.pem
chmod 0640 /etc/mikun2n-diagnostics/{cert,key}.pem
chmod 0600 /etc/mikun2n-diagnostics/client.json /etc/mikun2n-diagnostics/receiver.env
install -m 0755 "$SCRIPT_DIR/diagnostics-receiver.py" "$SCRIPT_DIR/read-diagnostics.py" /opt/mikun2n/diagnostics/
install -m 0644 "$SCRIPT_DIR/mikun2n-diagnostics.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable mikun2n-diagnostics.service
systemctl restart mikun2n-diagnostics.service
systemctl is-active --quiet mikun2n-diagnostics.service
echo "Installed HTTPS receiver on TCP 5443. Allow this port in the host/provider firewall."
echo "Private build input: /etc/mikun2n-diagnostics/client.json (root-only; never commit or print)."
echo "Logs: /var/lib/mikun2n-diagnostics/$BATCH/<session>/*.jsonl"
