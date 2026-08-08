# Remote SSH helper (workstation)

Lets the agent (or you) run commands on the VPS without typing the password each time. Secrets stay in local `.env` only.

## Setup

1. `.env` is **gitignored** — never commit it. Start from `.env.example` if needed.
2. Set:

```env
SSH_HOST=ujeen.pl
SSH_USER=root
SSH_PORT=22
SSH_PASSWORD=your-ssh-password
```

3. Install dependency once:

```bash
pip install -r scripts/requirements-remote.txt
```

## Commands

```bash
python scripts/remote.py --health
python scripts/remote.py -- 'cd /opt/CityChecker && docker compose -f docker-compose.yml -f docker-compose.prod.yml ps'
python scripts/remote.py -- 'cd /opt/CityChecker && ./scripts/backup.sh --prod'
```

`--health` checks uptime, disk, compose status, and `https://ujeen.pl/`.

## Cursor MCP (optional)

```bash
cp .cursor/mcp.json.example .cursor/mcp.json
# edit CITYCHECKER_PASSWORD (local API account)
```

`.cursor/mcp.json` is gitignored. MCP talks to **local** `http://localhost:8080`, not the VPS, unless you change `CITYCHECKER_BASE_URL`.
