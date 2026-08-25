#!/usr/bin/env pwsh
# Backup CityChecker → backups/citychecker-backup-YYYYMMDD-HHMM.tar.gz
# Cron (VPS): use backup.sh; on Windows run this manually.
param(
  [switch]$Prod
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $Root

# Keep in-container helper as LF even if the editor rewrote CRLF
python (Join-Path $PSScriptRoot "_write_pg_dump_helper.py") | Out-Null

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

$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmm")
$name = "citychecker-backup-$stamp"
$stage = Join-Path $Root "backups\$name"
$out = Join-Path $Root "backups\$name.tar.gz"

New-Item -ItemType Directory -Force -Path (Join-Path $stage "DataImports") | Out-Null

Write-Host "Waiting for db..."
Invoke-Dc @("up", "-d", "db")
Invoke-Dc @("exec", "-T", "db", "pg_isready", "-U", "citychecker", "-d", "citychecker")

Write-Host "Dumping Postgres (custom format, env cache excluded)..."
$helper = Join-Path $PSScriptRoot "_pg_dump_backup.sh"
Invoke-Dc @("cp", $helper, "db:/tmp/_pg_dump_backup.sh")
Invoke-Dc @("exec", "-T", "db", "sh", "/tmp/_pg_dump_backup.sh")
Invoke-Dc @("cp", "db:/tmp/citychecker.dump", (Join-Path $stage "postgres.dump"))
Invoke-Dc @("exec", "-T", "db", "rm", "-f", "/tmp/citychecker.dump", "/tmp/_pg_dump_backup.sh")

Write-Host "Copying DataImports..."
$src = Join-Path $Root "DataImports"
Get-ChildItem -Force $src | Where-Object {
  $_.Name -ne "__pycache__" -and $_.Name -notlike "_inspect*"
} | ForEach-Object {
  $dest = Join-Path (Join-Path $stage "DataImports") $_.Name
  Copy-Item -Recurse -Force $_.FullName $dest
}

$pgVer = (Invoke-Dc @("exec", "-T", "db", "psql", "-U", "citychecker", "-d", "citychecker", "-tAc", "SHOW server_version;")).ToString().Trim()
$gitSha = "unknown"
try { $gitSha = (git -C $Root rev-parse --short HEAD 2>$null).ToString().Trim() } catch { }

$manifest = @{
  format           = "citychecker-backup-v1"
  createdAt        = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
  gitSha           = $gitSha
  postgresVersion  = $pgVer
  excludedTables   = @("DistrictEnvironments", "CityEnvironmentSources", "OtodomPinSets", "OtodomPins", "districts_import_raw")
  secretsChecklist = @(
    "AUTH_JWT_SECRET", "GOOGLE_CLIENT_ID", "GOOGLE_ALLOWED_USER_ID",
    "CONTACT_EMAIL", "DOMAIN", "APP_PUBLIC_BASE_URL"
  )
  notes            = "Copy .env separately. Env risk and Otodom pin caches are omitted — refresh Environment mode / Otodom Refresh after restore."
} | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $stage "MANIFEST.json") -Value $manifest -Encoding utf8

Write-Host "Creating $out..."
if (Test-Path $out) { Remove-Item -Force $out }
Push-Location (Join-Path $Root "backups")
try {
  tar -czf $out $name
  if ($LASTEXITCODE -ne 0) { throw "tar failed (exit $LASTEXITCODE)" }
} finally {
  Pop-Location
}
Remove-Item -Recurse -Force $stage

Write-Host "OK: $out"
Get-Item $out | Format-List FullName, Length, LastWriteTime
