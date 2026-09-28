#!/usr/bin/env bash
# Install only the optional HTTPS test-log receiver. No supernode configuration is changed.
#   --community-file  accept uploads from clients that know a community listed there
#                     (default: the supernode's community list, when present)
#   --batch           additionally accept a private test batch with its own token
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST=""
BATCH=""
COMMUNITY_FILE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --host|--batch|--community-file)
            [ $# -ge 2 ] || { echo "Missing value for $1" >&2; exit 1; }
            case "$1" in --host) HOST="$2" ;; --batch) BATCH="$2" ;; --community-file) COMMUNITY_FILE="$2" ;; esac
            shift 2 ;;
        *) echo "Usage: bash $0 --host HOST [--community-file FILE] [--batch BATCH]" >&2; exit 1 ;;
    esac
done
[ "$EUID" -eq 0 ] || { echo "Run as root" >&2; exit 1; }
[[ "$HOST" =~ ^[a-zA-Z0-9.:-]+$ ]] || { echo "Supply an IP address or DNS hostname" >&2; exit 1; }
[ -z "$BATCH" ] || [[ "$BATCH" =~ ^[a-zA-Z0-9-]{1,64}$ ]] || { echo "Invalid batch id" >&2; exit 1; }
if [ -z "$COMMUNITY_FILE" ] && [ -f /etc/n3n/mikun2n-community.list ]; then
    COMMUNITY_FILE=/etc/n3n/mikun2n-community.list
fi
[ -n "$BATCH" ] || [ -n "$COMMUNITY_FILE" ] || { echo "Supply --community-file or --batch" >&2; exit 1; }
[ -z "$COMMUNITY_FILE" ] || [ -r "$COMMUNITY_FILE" ] || { echo "Community file $COMMUNITY_FILE is not readable" >&2; exit 1; }
for tool in python3 openssl systemctl; do command -v "$tool" >/dev/null; done
if ! id mikun2n-diag >/dev/null 2>&1; then
    useradd --system --user-group --no-create-home --shell /usr/sbin/nologin mikun2n-diag
fi
install -d -m 0750 -o root -g mikun2n-diag /etc/mikun2n-diagnostics
install -d -m 0755 /opt/mikun2n/diagnostics
python3 - "$HOST" "$BATCH" "$COMMUNITY_FILE" <<'PY'
import hashlib, ipaddress, json, os, pathlib, secrets, ssl, subprocess, sys
host, batch, community_file = sys.argv[1:]
root = pathlib.Path('/etc/mikun2n-diagnostics')
os.umask(0o077)
cert_path = root / 'cert.pem'
if not cert_path.exists():
    try:
        ipaddress.ip_address(host)
        san = 'IP:' + host
    except ValueError:
        san = 'DNS:' + host
    subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:3072', '-nodes', '-days', '825',
                    '-subj', '/CN=MikuN2N diagnostics', '-addext', 'subjectAltName=' + san,
                    '-keyout', str(root/'key.pem'), '-out', str(cert_path)], check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
url_host = f'[{host}]' if ':' in host else host
env = {}
env_path = root / 'receiver.env'
if env_path.exists():
    env = dict(line.split('=', 1) for line in env_path.read_text().splitlines() if '=' in line)
profile = root / 'client.json'
if batch:
    if profile.exists():
        old = json.loads(profile.read_text())
        if old['BatchId'] != batch or old['UploadUrl'] != f'https://{url_host}:5443/':
            raise SystemExit('Existing receiver belongs to another batch/host; preserve it and select a new deployment explicitly.')
        old['RetentionDays'] = 1
        profile.write_text(json.dumps(old), encoding='utf-8')
    else:
        token = secrets.token_hex(32)
        cert = ssl.PEM_cert_to_DER_cert(cert_path.read_text())
        profile.write_text(json.dumps({'BatchId': batch, 'UploadUrl': f'https://{url_host}:5443/',
                                       'CertificateSha256': hashlib.sha256(cert).hexdigest(),
                                       'UploadToken': token, 'RetentionDays': 1}), encoding='utf-8')
        env['MIKUN2N_DIAGNOSTICS_BATCH'] = batch
        env['MIKUN2N_DIAGNOSTICS_TOKEN_SHA256'] = hashlib.sha256(token.encode()).hexdigest()
if community_file:
    env['MIKUN2N_DIAGNOSTICS_COMMUNITY_FILE'] = community_file
env_path.write_text(''.join(f'{key}={value}\n' for key, value in env.items()))
cert = ssl.PEM_cert_to_DER_cert(cert_path.read_text())
print('Certificate SHA-256 (clients show this when asking for consent): ' + hashlib.sha256(cert).hexdigest())
PY
chown root:mikun2n-diag /etc/mikun2n-diagnostics/{cert,key}.pem
chmod 0640 /etc/mikun2n-diagnostics/{cert,key}.pem
chmod 0600 /etc/mikun2n-diagnostics/receiver.env
[ ! -f /etc/mikun2n-diagnostics/client.json ] || chmod 0600 /etc/mikun2n-diagnostics/client.json
install -m 0755 "$SCRIPT_DIR/diagnostics-receiver.py" "$SCRIPT_DIR/read-diagnostics.py" /opt/mikun2n/diagnostics/
install -m 0644 "$SCRIPT_DIR/mikun2n-diagnostics.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable mikun2n-diagnostics.service
systemctl restart mikun2n-diagnostics.service
systemctl is-active --quiet mikun2n-diagnostics.service
if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
    ufw allow 5443/tcp comment 'MikuN2N diagnostics receiver' >/dev/null
fi
echo "Installed HTTPS receiver on TCP 5443. Allow this port in the provider security group."
[ -z "$COMMUNITY_FILE" ] || echo "Community uploads: /var/lib/mikun2n-diagnostics/c-<hash of community>/<session>/*.jsonl"
[ -z "$BATCH" ] || echo "Private build input: /etc/mikun2n-diagnostics/client.json (root-only; never commit or print)."
