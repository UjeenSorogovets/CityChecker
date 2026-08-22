#!/usr/bin/env python3
"""git pull + compose rebuild on prod. Uses SSH_* from local .env (gitignored).

Commit and push to origin/main first. This does not copy files from your PC
(that left the server working tree dirty and blocked later pulls).

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
        import paramiko
    except ImportError:
        print("Install paramiko: pip install -r scripts/requirements-remote.txt", file=sys.stderr)
        return 2

    cmd = (
        "set -e; "
        f"cd {REMOTE_ROOT}; "
        "git pull --ff-only; "
        f"{COMPOSE} up --build -d; "
        f"{COMPOSE} ps; "
        "curl -sI --max-time 20 https://ujeen.pl/ | head -n 8 || true"
    )
    print(f"git pull --ff-only + compose rebuild on {user}@{host}:{REMOTE_ROOT}")

    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    try:
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
        stdin, stdout, stderr = client.exec_command(cmd, get_pty=True)
        out = stdout.read().decode("utf-8", errors="replace")
        err = stderr.read().decode("utf-8", errors="replace")
        code = stdout.channel.recv_exit_status()
        if out:
            sys.stdout.buffer.write(out.encode("utf-8", errors="replace"))
            if not out.endswith("\n"):
                sys.stdout.buffer.write(b"\n")
        if err:
            sys.stderr.buffer.write(err.encode("utf-8", errors="replace"))
            if not err.endswith("\n"):
                sys.stderr.buffer.write(b"\n")
        if code != 0:
            print(
                "Pull failed? The last file-upload deploy left local edits on the "
                "server. Commit+push from your PC, then say if you want those "
                "server edits discarded (keep .env).",
                file=sys.stderr,
            )
        return code
    finally:
        client.close()


if __name__ == "__main__":
    raise SystemExit(main())
