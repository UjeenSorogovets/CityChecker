# Start a local TCP proxy via Cloudflare Tunnel for SSH access to the new server.
# Requires: cloudflared installed locally (winget install cloudflare.cloudflared)
#
# This creates a local proxy on port 2222 that forwards to ssh.ujeen.pl via Cloudflare Tunnel.
# Then you can:
#   ssh -p 2222 root@localhost
#   or set SSH_HOST=localhost SSH_PORT=2222 in .env for deploy.py/remote.py
#
# Usage: .\scripts\ssh-tunnel.ps1

$tunnelHost = "ssh.ujeen.pl"
$localPort = 2222

Write-Host "Starting Cloudflare Tunnel proxy: localhost:$localPort -> $tunnelHost:22"
Write-Host "Use: ssh -p $localPort root@localhost"
Write-Host "Or set SSH_HOST=localhost, SSH_PORT=$localPort in .env"
Write-Host "Press Ctrl+C to stop."
cloudflared access tcp --hostname $tunnelHost --url localhost:$localPort
