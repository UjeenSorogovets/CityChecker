# AI documentation (Cursor / coding agents)

Detailed project context for AI systems. Humans: [../../README.md](../../README.md) · [ops](../README.md). **Keep this folder in sync when behavior changes.**

| File | Purpose |
|------|---------|
| [00-overview.md](00-overview.md) | Product, stack, repo layout, city GUIDs |
| [01-conventions.md](01-conventions.md) | How to change code (minimal diff, auth, i18n) |
| [02-architecture.md](02-architecture.md) | Startup, auth, data model, API map, migrations |
| [03-frontend.md](03-frontend.md) | Map, sheet, FAB, auth UI, voice, modes |
| [04-environment.md](04-environment.md) | Environment risk layer, wind wedges, cache |
| [05-data-imports.md](05-data-imports.md) | District import, DataImports files |
| [06-ops.md](06-ops.md) | Deploy, backup, SSH probe, MCP, debug tools |
| [07-gotchas.md](07-gotchas.md) | Known pitfalls and past bugs |

Root [AGENTS.md](../../AGENTS.md) is a short pointer; prefer this folder for depth.

**Cursor:** `.cursor/rules/citychecker-ai-context.mdc` (`alwaysApply`) points here.
