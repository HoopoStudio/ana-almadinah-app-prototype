#!/usr/bin/env bash
# Pull the latest version and restart. Run with sudo.
set -euo pipefail
git -C /opt/ana-almadinah-queue pull --ff-only
chown -R queue:queue /opt/ana-almadinah-queue
systemctl restart ana-almadinah-queue
sleep 1 && systemctl --no-pager --lines=5 status ana-almadinah-queue
