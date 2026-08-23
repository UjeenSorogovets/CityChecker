# AGENTS.md — AI entry point

Detailed AI docs: **[docs/ai/](docs/ai/README.md)** — read before non-trivial work.

Humans: [README.md](README.md) · [docs/](docs/README.md).

## Always-on rules

- Minimal diff; reuse existing patterns; English in code/comments/commits
- No commit/push unless the user asks
- Never commit or print `.env` / `SSH_PASSWORD` / JWT secrets
- Endpoints: `Map*Endpoints` + `EnsureOwner`; SPA in `wwwroot` (no bundler)
- i18n: add EN + RU together
- After point note save/delete: `reloadDistrictColors()` + `loadPointNotes()`
- District GUIDs change on re-import — restore DB dump, not GeoJSON alone
- JWT in `sessionStorage` (`cc_id_token`); env load uses `envLoadGen`, not `mapAbort`
- Do not call `bringToFront` on `L.LayerGroup`

## Quick pointers

| Need | File |
|------|------|
| Product / layout | [docs/ai/00-overview.md](docs/ai/00-overview.md) |
| Change recipes | [docs/ai/01-conventions.md](docs/ai/01-conventions.md) |
| API / DB / auth | [docs/ai/02-architecture.md](docs/ai/02-architecture.md) |
| Map / sheet / voice / modes | [docs/ai/03-frontend.md](docs/ai/03-frontend.md) |
| Environment layer | [docs/ai/04-environment.md](docs/ai/04-environment.md) |
| DataImports | [docs/ai/05-data-imports.md](docs/ai/05-data-imports.md) |
| Deploy / backup / SSH / MCP | [docs/ai/06-ops.md](docs/ai/06-ops.md) |
| Gotchas | [docs/ai/07-gotchas.md](docs/ai/07-gotchas.md) |

Local: http://localhost:8080 · Prod: https://ujeen.pl (`/opt/CityChecker`).
