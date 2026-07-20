#!/usr/bin/env sh
set -e
cd "$(dirname "$0")"

[ -f .env ] || { cp .env.example .env && echo "Created .env from .env.example"; }

docker compose up --build
