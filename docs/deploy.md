# Production deploy (VPS)

Target: **https://ujeen.pl** · app path `/opt/CityChecker` · SSH `root@ujeen.pl`

Stack: Caddy **80/443** (host Certbot certs) → API **8080** (internal only) + PostGIS.

## First-time server

```bash
# DNS: A/AAAA for ujeen.pl and www.ujeen.pl → server IP
apt install certbot
# stop anything on 80/443 first (e.g. host nginx)
certbot certonly --standalone -d ujeen.pl -d www.ujeen.pl

cd /opt/CityChecker
cp .env.example .env
# set DOMAIN, APP_PUBLIC_BASE_URL, AUTH_JWT_SECRET, CONTACT_EMAIL
```

Firewall: allow **80** and **443**. Do not publish API **8080** publicly (prod compose resets it).

## Deploy / update

```bash
ssh root@ujeen.pl
cd /opt/CityChecker
git pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps
curl -sI https://ujeen.pl/
```

Or from repo: `./run-prod.sh` / `.\run-prod.ps1`.

Hard-refresh the browser after deploy. Migrations run on API startup; the `pgdata` volume is kept.

## Cert renew

After host Certbot renew:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml exec caddy caddy reload --config /etc/caddy/Caddyfile
```

## Google OAuth (optional)

Add `https://ujeen.pl` and `https://www.ujeen.pl` to Authorized JavaScript origins. Email/password works without Google.

## Common failure: host nginx vs Caddy

If the site shows the default nginx page or HTTPS fails:

1. Host **nginx** is often bound to port 80 → Caddy cannot publish `80:80` / `443:443`.
2. Fix:

```bash
systemctl stop nginx
systemctl disable nginx
cd /opt/CityChecker
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --force-recreate caddy
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps
# Caddy PORTS should show 0.0.0.0:80->80 and 0.0.0.0:443->443
curl -sI https://ujeen.pl/
```

## Logs

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml logs -f caddy api
```
