#!/usr/bin/env python3
"""git pull + compose rebuild on prod. Uses SSH_* from local .env (gitignored).

Commit and push to origin/main first.

Usage (from repo root):
  python scripts/deploy.py
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REMOTE_ROOT = "/opt/CityChecker"
COMPOSE = "docker compose -f docker-compose.yml -f docker-compose.prod.yml"


def load_env(path: Path) -> dict[str, str]:
    out: dict[str, str] = {}
    if not path.is_file():
        return out
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        out[k.strip()] = v.strip().strip('"').strip("'")
    return out


def ssh_connect(host: str, port: int, user: str, password: str):
    import paramiko

    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(
        hostname=host,
        port=port,
        username=user,
        password=password,
        timeout=30,
        allow_agent=False,
        look_for_keys=False,
    )
    transport = client.get_transport()
    if transport:
        transport.set_keepalive(30)
        transport.banner_timeout = 60
        transport.auth_timeout = 60
    return client


def run_stream(client, cmd: str) -> int:
    stdin, stdout, stderr = client.exec_command(cmd, get_pty=True)
    chan = stdout.channel
    while True:
        if chan.recv_ready():
            sys.stdout.buffer.write(chan.recv(4096))
            sys.stdout.buffer.flush()
        if chan.recv_stderr_ready():
            sys.stderr.buffer.write(chan.recv_stderr(4096))
            sys.stderr.buffer.flush()
        if chan.exit_status_ready() and not chan.recv_ready() and not chan.recv_stderr_ready():
            break
    leftover = stdout.read()
    if leftover:
        sys.stdout.buffer.write(leftover if isinstance(leftover, bytes) else leftover.encode("utf-8", errors="replace"))
        sys.stdout.buffer.flush()
    err = stderr.read()
    if err:
        sys.stderr.buffer.write(err if isinstance(err, bytes) else err.encode("utf-8", errors="replace"))
        sys.stderr.buffer.flush()
    return chan.recv_exit_status()


def main() -> int:
    env = load_env(ROOT / ".env")
    host = env.get("SSH_HOST") or os.environ.get("SSH_HOST")
    user = env.get("SSH_USER") or os.environ.get("SSH_USER") or "root"
    password = env.get("SSH_PASSWORD") or os.environ.get("SSH_PASSWORD")
    port = int(env.get("SSH_PORT") or os.environ.get("SSH_PORT") or "22")

    if not host:
        print("SSH_HOST missing in .env", file=sys.stderr)
        return 2
    if not password:
        print("SSH_PASSWORD missing in .env (local only, never commit)", file=sys.stderr)
        return 2

    try:
        import paramiko  # noqa: F401
    except ImportError:
        print("Install paramiko: pip install -r scripts/requirements-remote.txt", file=sys.stderr)
        return 2

    cmd = f"""
set -e
cd {REMOTE_ROOT}
cp -a .env /root/citychecker.env.bak
git fetch origin
git restore --worktree --staged -- . || true
git pull --ff-only origin main
cp -a /root/citychecker.env.bak .env
{COMPOSE} up --build -d
{COMPOSE} ps
git log -1 --oneline
for i in 1 2 3 4 5 6; do
  code=$(curl -sI --max-time 15 https://ujeen.pl/ | head -n 1 || true)
  echo "$code"
  echo "$code" | grep -q " 200 " && break
  sleep 5
done
"""
    print(f"Deploy {user}@{host}:{REMOTE_ROOT} (git pull + rebuild)")
    client = ssh_connect(host, port, user, password)
    try:
        return run_stream(client, cmd)
    finally:
        client.close()


if __name__ == "__main__":
    raise SystemExit(main())
