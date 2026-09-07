#!/usr/bin/env bash
# Run with sudo from a directory containing the published app in ./app.
set -euo pipefail
[[ $EUID == 0 ]] || { echo 'Run this script with sudo.' >&2; exit 1; }
cd -- "$(dirname -- "$(readlink -f -- "$0")")"
test -f app/Enactive.Server
test -f app/wwwroot/index.html
if [[ -e /etc/systemd/system/enactive-server.service || -e /opt/enactive-server ]]; then
    echo 'Existing installation found; refusing to overwrite it.' >&2
    exit 1
fi
apt-get update
apt-get install -y ca-certificates curl libicu74 libssl3t64 zlib1g openssl
install -d -m 0755 /usr/share/keyrings
curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg -o /usr/share/keyrings/cloudflare-main.gpg
chmod 0644 /usr/share/keyrings/cloudflare-main.gpg
printf '%s\n' 'deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared any main' > /etc/apt/sources.list.d/cloudflared.list
apt-get update
apt-get install -y cloudflared
if ! id enactive-server >/dev/null 2>&1; then
    useradd --system --home-dir /var/lib/enactive-server --shell /usr/sbin/nologin enactive-server
fi
install -d -o root -g root -m 0755 /opt/enactive-server
cp -R app/. /opt/enactive-server/
chown -R root:root /opt/enactive-server
chmod -R go-w /opt/enactive-server
chmod 0755 /opt/enactive-server/Enactive.Server
install -d -o enactive-server -g enactive-server -m 0700 /var/lib/enactive-server
install -d -o root -g root -m 0700 /etc/enactive-server
if [[ ! -f /etc/enactive-server/server.env ]]; then
    umask 077
    printf 'ENACTIVE_OWNER_KEY=%s\n' "$(openssl rand -hex 32)" > /etc/enactive-server/server.env
fi
cat > /etc/systemd/system/enactive-server.service <<'UNIT'
[Unit]
Description=Enactive Remote Server
After=network.target

[Service]
User=enactive-server
Group=enactive-server
WorkingDirectory=/opt/enactive-server
ExecStart=/opt/enactive-server/Enactive.Server
EnvironmentFile=/etc/enactive-server/server.env
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5187
Environment=ASPNETCORE_HTTPS_PORT=443
Environment=ENACTIVE_LOCAL_PROXY=true
Environment=ENACTIVE_DATA=/var/lib/enactive-server
Environment=AllowedHosts=remote.enactive.dev
Restart=on-failure
RestartSec=5
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/enactive-server

[Install]
WantedBy=multi-user.target
UNIT
chmod 0644 /etc/systemd/system/enactive-server.service
systemctl daemon-reload
systemctl enable --now enactive-server
for attempt in {1..30}; do
    if curl --fail --silent -H 'Host: remote.enactive.dev' -H 'X-Forwarded-Proto: https' http://127.0.0.1:5187/health; then
        printf '\nEnactive is ready. Owner key: sudo cat /etc/enactive-server/server.env\n'
        exit 0
    fi
    sleep 1
done
systemctl status enactive-server --no-pager
exit 1
