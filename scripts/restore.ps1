#!/usr/bin/env pwsh
# Restore CityChecker from a backup archive.
# Usage: .\scripts\restore.ps1 backups\citychecker-backup-YYYYMMDD-HHMM.tar.gz [-Prod]
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [string]$Archive,
  [switch]$Prod
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $Root

$ComposeArgs = @("compose")
if ($Prod) {
  $ComposeArgs = @("compose", "-f", "docker-compose.yml", "-f", "docker-compose.prod.yml")
}

function Invoke-Dc([string[]]$DcArgs) {
  & docker @ComposeArgs @DcArgs
  if ($LASTEXITCODE -ne 0) {
    throw "docker $(($ComposeArgs + $DcArgs) -join ' ') failed (exit $LASTEXITCODE)"
  }
}

$archiveAbs = if ([System.IO.Path]::IsPathRooted($Archive)) { $Archive } else { Join-Path $Root $Archive }
if (-not (Test-Path $archiveAbs)) { throw "Archive not found: $archiveAbs" }

$extract = Join-Path $Root ("backups\.restore-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force -Path $extract | Out-Null
try {
  Write-Host "Extracting $(Split-Path $archiveAbs -Leaf)..."
  tar -xzf $archiveAbs -C $extract
  if ($LASTEXITCODE -ne 0) { throw "tar extract failed (exit $LASTEXITCODE)" }

  $bundle = Get-ChildItem $extract -Directory | Select-Object -First 1
  if (-not $bundle -or -not (Test-Path (Join-Path $bundle.FullName "postgres.dump"))) {
    throw "Invalid bundle: expected citychecker-backup-*/postgres.dump"
  }

  $manifestPath = Join-Path $bundle.FullName "MANIFEST.json"
  if (Test-Path $manifestPath) {
    Write-Host "MANIFEST:"
    Get-Content $manifestPath -Raw
    Write-Host ""
  }

  if (-not (Test-Path (Join-Path $Root ".env"))) {
    Write-Warning ".env missing — copy secrets before relying on login/JWT (see MANIFEST secretsChecklist)."
  }

  Write-Host "Stopping api (db stays up)..."
  & docker @ComposeArgs stop api 2>$null | Out-Null
  Invoke-Dc @("up", "-d", "db")
  Invoke-Dc @("exec", "-T", "db", "pg_isready", "-U", "citychecker", "-d", "citychecker")

  Write-Host "Recreating database..."
  Invoke-Dc @(
    "exec", "-T", "db", "psql", "-U", "citychecker", "-d", "postgres", "-v", "ON_ERROR_STOP=1",
    "-c", "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'citychecker' AND pid <> pg_backend_pid();",
    "-c", "DROP DATABASE IF EXISTS citychecker;",
    "-c", "CREATE DATABASE citychecker OWNER citychecker;"
  )

  Write-Host "Restoring dump..."
  Invoke-Dc @("cp", (Join-Path $bundle.FullName "postgres.dump"), "db:/tmp/citychecker.dump")
  # pg_restore often exits 1 on non-fatal notices; verify tables afterward
  & docker @ComposeArgs exec -T db pg_restore -U citychecker -d citychecker --clean --if-exists --no-owner --no-acl /tmp/citychecker.dump
  Invoke-Dc @(
    "exec", "-T", "db", "psql", "-U", "citychecker", "-d", "citychecker", "-v", "ON_ERROR_STOP=1",
    "-c", 'SELECT COUNT(*) AS districts FROM "Districts"; SELECT COUNT(*) AS notes FROM "Notes";'
  )
  Invoke-Dc @("exec", "-T", "db", "rm", "-f", "/tmp/citychecker.dump")

  $bundleImports = Join-Path $bundle.FullName "DataImports"
  if (Test-Path $bundleImports) {
    Write-Host "Syncing DataImports from bundle..."
    $dest = Join-Path $Root "DataImports"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Recurse -Force (Join-Path $bundleImports "*") $dest
  }

  Write-Host "Starting stack..."
  Invoke-Dc @("up", "-d")
  Write-Host ""
  Write-Host "Restore done."
  Write-Host "  - Ensure .env is present (AUTH_JWT_SECRET etc.)."
  Write-Host "  - Env layer cache was not in the dump — open Environment mode or:"
  Write-Host "      POST /api/admin/refresh-environment/{cityId}"
}
finally {
  if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
}
