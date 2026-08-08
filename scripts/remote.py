#!/usr/bin/env python3
"""Run a command on the prod VPS using SSH_* from local .env (gitignored).

Usage (from repo root):
  python scripts/remote.py -- 'docker compose ps'
  python scripts/remote.py --health
"""
from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


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
    ap = argparse.ArgumentParser(description="SSH into prod using .env SSH_* vars")
    ap.add_argument("--health", action="store_true", help="uptime, compose ps, curl homepage")
    ap.add_argument(
        "remote_cmd",
        nargs=argparse.REMAINDER,
        help="command to run on the server (use -- before flags)",
    )
    args = ap.parse_args()

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

    if args.health:
        cmd = (
            "set -e; "
            "echo '=== uptime ==='; uptime; "
            "echo '=== disk ==='; df -h / | tail -1; "
            "cd /opt/CityChecker 2>/dev/null || cd /opt/citychecker 2>/dev/null || { echo 'NO /opt/CityChecker'; exit 1; }; "
            "echo '=== compose ps ==='; "
            "docker compose -f docker-compose.yml -f docker-compose.prod.yml ps; "
            "echo '=== https ==='; "
            "curl -sI --max-time 15 https://ujeen.pl/ | head -n 5 || true"
        )
    else:
        parts = list(args.remote_cmd)
        if parts and parts[0] == "--":
            parts = parts[1:]
        if not parts:
            ap.print_help()
            return 2
        cmd = " ".join(parts)

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
        stdin, stdout, stderr = client.exec_command(cmd, get_pty=True)
        out = stdout.read().decode("utf-8", errors="replace")
        err = stderr.read().decode("utf-8", errors="replace")
        code = stdout.channel.recv_exit_status()
        if out:
            sys.stdout.write(out)
            if not out.endswith("\n"):
                sys.stdout.write("\n")
        if err:
            sys.stderr.write(err)
            if not err.endswith("\n"):
                sys.stderr.write("\n")
        return code
    finally:
        client.close()


if __name__ == "__main__":
    raise SystemExit(main())
