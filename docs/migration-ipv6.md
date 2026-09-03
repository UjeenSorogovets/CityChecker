# Migration to IPv6-only Netcup VPS

Server IPv6: `2a03:4000:5:45a:38f6:99ff:fe1d:82fd`
Domain: `ujeen.pl` (unchanged)
IPv4 access: Cloudflare proxy

## Prerequisites

- Netcup VPS with Debian 13 minimal (done)
- Cloudflare account (free plan)
- Backup: `backups/citychecker-backup-20260903-1454.tar.gz` + `.env.prod`
- `cloudflared` on your workstation: `winget install cloudflare.cloudflared`

---

## Step 1: Add ujeen.pl to Cloudflare

1. Go to https://dash.cloudflare.com → **Add a site** → enter `ujeen.pl`
2. Select **Free** plan
3. Cloudflare shows nameservers (e.g. `isla.ns.cloudflare.com`, `phil.ns.cloudflare.com`)
4. Go to your domain registrar and change NS to the Cloudflare nameservers
5. Wait for NS propagation (can take up to 24h, usually ~1h)
6. In Cloudflare dashboard for ujeen.pl:
   - **SSL/TLS** → set mode to **Full (Strict)**
   - **DNS** → Add records:
     - `AAAA` | `ujeen.pl` | `2a03:4000:5:45a:38f6:99ff:fe1d:82fd` | Proxied
     - `AAAA` | `www` | `2a03:4000:5:45a:38f6:99ff:fe1d:82fd` | Proxied

## Step 2: Cloudflare Origin Certificate

1. In Cloudflare → **SSL/TLS** → **Origin Server** → **Create Certificate**
2. Leave defaults (RSA, 15 years, `*.ujeen.pl` + `ujeen.pl`)
3. Copy the **Origin Certificate** (PEM) and **Private Key** (PEM)
4. Save them — you'll install them on the server in Step 5

## Step 3: Set up Cloudflare Tunnel (for SSH from IPv4 workstation)

On the server (via Netcup VNC console):

```bash
cloudflared tunnel login
# Opens a browser URL — paste it into a browser on any machine, authorize ujeen.pl

cloudflared tunnel create citychecker-ssh

# Note the tunnel UUID (e.g. abc123-...)
TUNNEL_ID=<paste-uuid-here>

mkdir -p /etc/cloudflared
cat > /etc/cloudflared/config.yml <<EOF
tunnel: $TUNNEL_ID
credentials-file: /root/.cloudflared/$TUNNEL_ID.json

ingress:
  - hostname: ssh.ujeen.pl
    service: ssh://localhost:22
  - service: http_status:404
EOF

# Add DNS record for the tunnel
cloudflared tunnel route dns citychecker-ssh ssh.ujeen.pl

# Install as systemd service
cloudflared service install
systemctl enable --now cloudflared
```

Now from your Windows workstation:
```powershell
# Test SSH via tunnel
cloudflared access ssh --hostname ssh.ujeen.pl
# Or use the TCP proxy for paramiko:
.\scripts\ssh-tunnel.ps1
# Then in another terminal: ssh -p 2222 root@localhost
```

## Step 4: Provision server

Via Netcup VNC console (or SSH via tunnel once Step 3 is done):

```bash
bash /dev/stdin < <(curl -fsSL https://raw.githubusercontent.com/<user>/CityChecker/main/scripts/provision-new-server.sh)
# OR copy-paste the script content from scripts/provision-new-server.sh
```

Or manually:
```bash
apt update && apt upgrade -y
apt install -y docker.io docker-compose-plugin git curl ufw rsync nano ca-certificates gnupg
systemctl enable --now docker

# Docker IPv6
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

# Firewall
ufw allow ssh
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable
```

## Step 5: Install origin TLS cert

```bash
mkdir -p /etc/letsencrypt/live/ujeen.pl
# Paste the cert and key from Step 2:
nano /etc/letsencrypt/live/ujeen.pl/fullchain.pem
nano /etc/letsencrypt/live/ujeen.pl/privkey.pem
chmod 600 /etc/letsencrypt/live/ujeen.pl/privkey.pem
```

## Step 6: Clone and restore

```bash
cd /opt
git clone https://github.com/<user>/CityChecker.git
cd CityChecker
```

Upload backup from workstation (via tunnel proxy):
```powershell
# Start tunnel in one terminal:
.\scripts\ssh-tunnel.ps1
# In another terminal:
scp -P 2222 backups/citychecker-backup-20260903-1454.tar.gz root@localhost:/opt/CityChecker/
scp -P 2222 backups/.env.prod root@localhost:/opt/CityChecker/.env
```

On server:
```bash
cd /opt/CityChecker
# Start DB, restore
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d db
sleep 10  # wait for PG ready
bash scripts/restore.sh /opt/CityChecker/citychecker-backup-20260903-1454.tar.gz --prod
```

## Step 7: Start full stack

```bash
cd /opt/CityChecker
docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps
```

Verify locally on server:
```bash
curl -sI http://localhost:8080/
# Should return 200
```

## Step 8: DNS cutover

If ujeen.pl was previously on another provider's DNS:
- NS change to Cloudflare (Step 1) + AAAA records pointing to new server is the cutover
- Remove any old A records (IPv4 of old server)

If already on Cloudflare, just update AAAA to the new IPv6.

Verify:
```bash
curl -sI https://ujeen.pl/
# Should return 200
```

## Step 9: Update workstation .env for SSH

Edit `.env` in the repo root:
```
SSH_HOST=localhost
SSH_PORT=2222
```

Then `python scripts/deploy.py` and `python scripts/remote.py --health` will work
via the Cloudflare Tunnel (run `.\scripts\ssh-tunnel.ps1` first).

## Step 10: Refresh building footprints

```bash
# From workstation (after DNS cutover):
curl -X POST https://ujeen.pl/api/admin/refresh-building-footprints/<wolomin-city-id> -H "Authorization: Bearer <token>"
curl -X POST https://ujeen.pl/api/admin/refresh-building-footprints/<wroclaw-city-id> -H "Authorization: Bearer <token>"
```

## Step 11: Verify & shut down old server

1. Test all functionality: login, map, notes, environment, building footprints
2. Google OAuth: ensure `https://ujeen.pl` is still in Google Cloud Console authorized origins
3. Cancel / destroy old VPS

## Troubleshooting

### NAT64 not working (can't reach IPv4 endpoints from server)

```bash
# Try Google's public DNS64:
echo "nameserver 2001:4860:4860::6464" > /etc/resolv.conf
# Test:
curl https://overpass-api.de/api/status
```

### Docker containers can't reach IPv4

Ensure `/etc/docker/daemon.json` has `"ipv6": true` and restart Docker.
The host's NAT64/DNS64 must be accessible from within containers.

### Cloudflare SSL errors

- Ensure SSL mode is **Full (Strict)** (not Flexible)
- Ensure origin cert is installed and Caddy can read it
- Check: `docker compose ... logs caddy`
