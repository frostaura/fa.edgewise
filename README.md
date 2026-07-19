# Edgewise

**The journal that grades your discipline, not just your P&L.**

Edgewise is a self-hosted trading journal and discipline coach. Every trade is scored against your own written plan by a versioned adherence rubric — so you can tell the difference between a good process with a bad outcome and a bad process that got lucky.

## Pillars

| Pillar | What it does |
| --- | --- |
| **Journal** | Trade plans, executions, notes, attachments — every trade graded A–F on adherence ([rubric v1](docs/adherence-rubric-v1.md)) |
| **Coach** | AI coach (Claude) grounded in your own journal data; streams advice, never directives |
| **Portfolio** | Accounts, positions, exposure/heat tracking, equity snapshots |
| **Radar** | Watchlists, price/news alerts, economic calendar, Fear & Greed context |
| **Lab** | Experiments and setup analytics over your journal history |
| **Cockpit** | The daily dashboard tying it all together |

## Tech stack

- **API** — .NET 10 (ASP.NET Core minimal APIs), EF Core + PostgreSQL, Hangfire background jobs, MCP endpoint (`/mcp`)
- **Web** — Vite + React SPA (PWA), served by nginx in production
- **Deploy** — Docker images published to Docker Hub by GitHub Actions; runs as a Portainer stack ([docker-compose.yml](docker-compose.yml))

## Local development

Prereqs: .NET 10 SDK, Node 22, Docker (for Postgres / Testcontainers).

```bash
# API — listens on http://localhost:5210 (health: /health)
dotnet run --project src/api/Edgewise.Api

# Web — http://localhost:5173, dev server proxies /api to the API
cd src/web
npm install
npm run dev
```

The API needs at minimum `ConnectionStrings__Default`, `EDGEWISE_JWT_SECRET`, and `EDGEWISE_ENCRYPTION_KEY` (see [.env.example](.env.example) for all variables and generation commands). Migrations run automatically at startup.

## Testing

```bash
# API: format check, unit + integration tests
dotnet format Edgewise.slnx --verify-no-changes
dotnet test Edgewise.slnx
# Integration tests spin up Postgres via Testcontainers unless EDGEWISE_TEST_DB
# points at an existing database.

# Web
cd src/web
npm run lint && npm run typecheck && npm run test && npm run build
```

CI ([build-edgewise.yml](.github/workflows/build-edgewise.yml)) runs both suites on every PR, plus a design-token gate that rejects raw color/size literals outside `components/ui`. Merges to `main` (or a manual dispatch) publish multi-arch `edgewise-api` and `edgewise-web` images tagged `latest` + commit SHA.

## Deployment

Deploy as a Portainer stack using [docker-compose.yml](docker-compose.yml) — Postgres, API, and web behind a single published port. Full walkthrough, secret generation, backups, and upgrades: **[docs/deployment-portainer.md](docs/deployment-portainer.md)**.

## Docs

- [Architecture](docs/architecture.md)
- [Data model](docs/data-model.md)
- [Adherence rubric v1](docs/adherence-rubric-v1.md)
- [Market data providers](docs/market-data-providers.md)
- [Portainer deployment](docs/deployment-portainer.md)
