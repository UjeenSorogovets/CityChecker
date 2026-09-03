#!/usr/bin/env bash
# Provision a fresh Debian 13 (Trixie) minimal VPS for CityChecker.
# Run as root from the Netcup VNC console or SSH.
# After this script, the server is ready for: git clone, restore, compose up.
set -euo pipefail

echo "=== 1. System update ==="
apt update && apt upgrade -y

echo "=== 2. Install essentials ==="
apt install -y \
  docker.io docker-compose-plugin \
  git curl ufw rsync nano \
  ca-certificates gnupg

echo "=== 3. Enable & start Docker ==="
systemctl enable --now docker

echo "=== 4. Configure Docker for IPv6 ==="
mkdir -p /etc/docker
cat > /etc/docker/daemon.json <<'JSON'
{
  "ipv6": true,
  "fixed-cidr-v6": "fd00:d0ck:er::/48",
  "ip6tables": true,
  "experimental": true
}
JSON
systemctl restart docker

echo "=== 5. Verify outbound connectivity ==="
echo "IPv6 test:"
curl -6 -sI --max-time 10 https://ipv6.google.com/ | head -3 || echo "WARN: IPv6 outbound failed"
echo "IPv4-via-NAT64 test:"
curl -sI --max-time 10 https://overpass-api.de/api/status | head -3 || echo "WARN: outbound IPv4 (NAT64) may not work — check DNS64 config"

echo "=== 6. Firewall ==="
ufw allow ssh
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable
ufw status

echo "=== 7. Install Cloudflare Tunnel (cloudflared) ==="
# For SSH access from IPv4-only workstations
curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg \
  | gpg --dearmor -o /usr/share/keyrings/cloudflare-main.gpg
echo "deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared any main" \
  > /etc/apt/sources.list.d/cloudflared.list
apt update && apt install -y cloudflared

echo "=== 8. Create /opt/CityChecker ==="
mkdir -p /opt/CityChecker

echo ""
echo "========================================="
echo " Server provisioned successfully!"
echo "========================================="
echo ""
echo "Next steps:"
echo "  1. Set up Cloudflare Tunnel for SSH:"
echo "     cloudflared tunnel login"
echo "     cloudflared tunnel create citychecker-ssh"
echo "     # Then configure tunnel (see docs below)"
echo ""
echo "  2. Clone repo:"
echo "     cd /opt"
echo "     git clone https://github.com/<user>/CityChecker.git"
echo ""
echo "  3. Upload backup + .env, restore DB"
echo "  4. docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d"
echo ""
echo "If NAT64 outbound failed, configure DNS64:"
echo "  echo 'nameserver 2001:4860:4860::6464' > /etc/resolv.conf"
echo "  # or add to /etc/systemd/resolved.conf and restart systemd-resolved"
