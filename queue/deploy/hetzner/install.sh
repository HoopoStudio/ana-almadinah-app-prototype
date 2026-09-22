#!/usr/bin/env bash
# First-time install on Ubuntu/Debian (run as root or with sudo). Safe to re-run.
#   curl -fsSL https://raw.githubusercontent.com/HoopoStudio/ana-almadinah-queue/main/deploy/hetzner/install.sh | sudo bash
set -euo pipefail
REPO=https://github.com/HoopoStudio/ana-almadinah-queue.git
APP=/opt/ana-almadinah-queue
DATA=/var/lib/ana-almadinah-queue
ENV=/etc/ana-almadinah-queue.env

# 1. Node.js 22 (skipped if a recent Node is already present)
if ! command -v node >/dev/null || [ "$(node -p 'process.versions.node.split(".")[0]')" -lt 18 ]; then
  curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
  apt-get install -y nodejs
fi
apt-get install -y git nginx

# 2. Dedicated user, code, data folder
id -u queue >/dev/null 2>&1 || useradd --system --home "$APP" --shell /usr/sbin/nologin queue
if [ -d "$APP/.git" ]; then git -C "$APP" pull --ff-only; else git clone "$REPO" "$APP"; fi
mkdir -p "$DATA"
chown -R queue:queue "$APP" "$DATA"

# 3. Environment file (created once, then edited by you)
if [ ! -f "$ENV" ]; then
  cp "$APP/deploy/hetzner/queue.env.example" "$ENV"
  chmod 600 "$ENV"
  echo ">> Created $ENV — edit STAFF_PIN and the ODOO_* values, then: systemctl restart ana-almadinah-queue"
fi

# 4. Service
cp "$APP/deploy/hetzner/ana-almadinah-queue.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now ana-almadinah-queue
systemctl restart ana-almadinah-queue

# 5. Nginx site (HTTPS is added by certbot afterwards)
if [ ! -f /etc/nginx/sites-available/queue ]; then
  cp "$APP/deploy/hetzner/nginx-queue.conf" /etc/nginx/sites-available/queue
  ln -sf /etc/nginx/sites-available/queue /etc/nginx/sites-enabled/queue
  nginx -t && systemctl reload nginx
fi

sleep 1
curl -fsS http://127.0.0.1:3000/api/summary >/dev/null && echo ">> Queue app is running on port 3000."
echo ">> Next: point queue.ana-almadinahart.com at this server, then run:"
echo "     sudo apt-get install -y certbot python3-certbot-nginx && sudo certbot --nginx -d queue.ana-almadinahart.com"
