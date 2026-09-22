# Deploying on the Hetzner server (next to Odoo)

Assumes Ubuntu or Debian with Odoo installed directly. The queue app runs as its own
service on port 3000; Odoo and its configuration are not touched.

## First time (about 15 minutes)

1. **DNS**: add an `A` record `queue.ana-almadinahart.com` → the server's IP
   (and `AAAA` for IPv6 if the server has one). Wait until `ping queue.ana-almadinahart.com`
   answers from your laptop.
2. **SSH in** and run the installer:
   ```bash
   curl -fsSL https://raw.githubusercontent.com/HoopoStudio/ana-almadinah-queue/main/deploy/hetzner/install.sh | sudo bash
   ```
   It installs Node.js and Nginx if missing, creates a `queue` system user, clones the
   app to `/opt/ana-almadinah-queue`, keeps data in `/var/lib/ana-almadinah-queue`,
   and starts the `ana-almadinah-queue` service.
3. **Set your PIN and Odoo access**:
   ```bash
   sudo nano /etc/ana-almadinah-queue.env
   sudo systemctl restart ana-almadinah-queue
   ```
   `ODOO_URL=http://127.0.0.1:8069` talks to Odoo locally. If Odoo listens on another
   port or runs in Docker, use `https://ana-almadinahart.com` instead.
4. **HTTPS** (free, renews itself):
   ```bash
   sudo apt-get install -y certbot python3-certbot-nginx
   sudo certbot --nginx -d queue.ana-almadinahart.com
   ```
5. Open `https://queue.ana-almadinahart.com/staff`, enter the PIN, and press
   **فحص اتصال Odoo** in the settings to confirm the link to Odoo.

## Updating later

```bash
sudo /opt/ana-almadinah-queue/deploy/hetzner/update.sh
```

## Useful commands

```bash
sudo systemctl status ana-almadinah-queue        # is it running?
sudo journalctl -u ana-almadinah-queue -f        # live log (Odoo sync and e-mail errors show here)
ls /var/lib/ana-almadinah-queue                  # queue.json, guests.json, daily archives
```

## Backups

Add `/var/lib/ana-almadinah-queue` to whatever already backs up the Odoo database.
`guests.json` is the permanent guest list.

## If Odoo runs in Docker

Everything above still applies. Only two things change: in the env file use the public
`ODOO_URL=https://ana-almadinahart.com`, and if Nginx itself runs inside Docker, put the
`nginx-queue.conf` server block into that container's config (proxying to the host's
port 3000, e.g. `http://host.docker.internal:3000` or the host's Docker bridge IP).
