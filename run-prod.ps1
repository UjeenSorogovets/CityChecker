#!/usr/bin/env pwsh
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path .env)) {
  Copy-Item .env.example .env
  Write-Host "Created .env from .env.example — set AUTH_JWT_SECRET and ACME_EMAIL"
}

docker compose -f docker-compose.yml -f docker-compose.prod.yml up --build -d
Write-Host ""
Write-Host "Production: https://ujeen.pl (after DNS + Let's Encrypt)"
Write-Host "Logs: docker compose -f docker-compose.yml -f docker-compose.prod.yml logs -f caddy api"
