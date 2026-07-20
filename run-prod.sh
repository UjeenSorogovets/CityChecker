#!/usr/bin/env sh
set -e
cd "$(dirname "$0")"

[ -f .env ] || { cp .env.example .env && echo "Created .env — set AUTH_JWT_SECRET and ACME_EMAIL"; }

docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d
echo ""
echo "Production: https://ujeen.pl (after DNS + Let's Encrypt)"
echo "Logs: docker compose -f docker-compose.yml -f docker-compose.prod.yml logs -f caddy api"
