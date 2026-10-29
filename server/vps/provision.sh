#!/bin/sh
set -eu
# Run as root after copying this directory to /opt/nowcast-covers.
APP=/opt/nowcast-covers
DOMAIN=nowcast.85-192-42-111.sslip.io
if ! id nowcast-covers >/dev/null 2>&1; then
    useradd --system --home /var/lib/nowcast-covers --shell /usr/sbin/nologin nowcast-covers
fi
install -d -o nowcast-covers -g nowcast-covers -m 750 /var/lib/nowcast-covers
install -d -m 755 /var/www/nowcast-acme /etc/nowcast-covers
if [ ! -f /etc/nowcast-covers/service.env ]; then
    python3 - <<'PY'
import os,secrets
path='/etc/nowcast-covers/service.env'
fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
with os.fdopen(fd,'w') as f:
    f.write('PUBLIC_ORIGIN=https://nowcast.85-192-42-111.sslip.io:10443\n')
    for key in ('ADMIN_KEY','BRIDGE_KEY','IP_SALT'):
        f.write(key+'='+secrets.token_urlsafe(36)+'\n')
PY
fi
cat > /etc/nginx/conf.d/nowcast-http.conf <<'EOF'
limit_req_zone $binary_remote_addr zone=nowcast_upload:10m rate=5r/s;
limit_req_zone $binary_remote_addr zone=nowcast_login:1m rate=6r/m;
limit_conn_zone $binary_remote_addr zone=nowcast_conn:10m;
server {
    listen 80;
    server_name nowcast.85-192-42-111.sslip.io;
    access_log off;
    location /.well-known/acme-challenge/ { root /var/www/nowcast-acme; }
    location / { return 301 https://nowcast.85-192-42-111.sslip.io:10443$request_uri; }
}
EOF
nginx -t
systemctl enable --now nginx
systemctl reload nginx
if [ ! -f /etc/letsencrypt/live/$DOMAIN/fullchain.pem ]; then
    certbot certonly --non-interactive --agree-tos --register-unsafely-without-email \
        --webroot -w /var/www/nowcast-acme -d "$DOMAIN"
fi
cat > /etc/nginx/conf.d/nowcast-https.conf <<'EOF'
server {
    listen 10443 ssl;
    server_name nowcast.85-192-42-111.sslip.io;
    ssl_certificate /etc/letsencrypt/live/nowcast.85-192-42-111.sslip.io/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/nowcast.85-192-42-111.sslip.io/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    server_tokens off;
    access_log off;
    client_max_body_size 256k;
    client_body_timeout 15s;
    limit_conn nowcast_conn 12;
    location = /login {
        limit_req zone=nowcast_login burst=4 nodelay;
        proxy_pass http://127.0.0.1:18761;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header Host $host;
    }
    # Gallery previews arrive together; the upload limiter must not reject reads.
    location ~ "^/[0-9a-f]{40}\.(jpg|png)$" {
        limit_conn nowcast_conn 80;
        proxy_pass http://127.0.0.1:18761;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header Host $host;
        proxy_read_timeout 20s;
    }
    location / {
        limit_req zone=nowcast_upload burst=20 nodelay;
        proxy_pass http://127.0.0.1:18761;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header Host $host;
        proxy_read_timeout 20s;
    }
}
EOF
cat > /etc/systemd/system/nowcast-covers.service <<'EOF'
[Unit]
Description=Nowcast covers and moderation
After=network.target
[Service]
User=nowcast-covers
Group=nowcast-covers
WorkingDirectory=/opt/nowcast-covers
EnvironmentFile=/etc/nowcast-covers/service.env
ExecStart=/usr/bin/python3 /opt/nowcast-covers/app.py
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/nowcast-covers
RestrictSUIDSGID=true
CapabilityBoundingSet=
UMask=0077
[Install]
WantedBy=multi-user.target
EOF
install -d /etc/letsencrypt/renewal-hooks/deploy
printf '#!/bin/sh\n/usr/sbin/nginx -t && /bin/systemctl reload nginx\n' > /etc/letsencrypt/renewal-hooks/deploy/nowcast-nginx
chmod 755 /etc/letsencrypt/renewal-hooks/deploy/nowcast-nginx
systemctl daemon-reload
systemctl enable --now nowcast-covers certbot.timer
nginx -t
systemctl reload nginx
python3 - <<'PY'
import os
values=dict(line.strip().split('=',1) for line in open('/etc/nowcast-covers/service.env') if '=' in line)
fd=os.open('/etc/nowcast-covers/admin-access.txt',os.O_WRONLY|os.O_CREAT|os.O_TRUNC,0o600)
with os.fdopen(fd,'w') as f:
    f.write('Админка: '+values['PUBLIC_ORIGIN']+'/admin\nКлюч: '+values['ADMIN_KEY']+'\n')
PY
echo 'Nowcast HTTPS and service installed.'
