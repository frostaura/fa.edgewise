# Deploying Edgewise with Portainer

Edgewise ships as two Docker images (`edgewise-api`, `edgewise-web`) plus stock
`postgres:16-alpine`, orchestrated by the repo-root [`docker-compose.yml`](../docker-compose.yml).
The stack is **pull-only** — Portainer never builds anything; CI publishes images.

## 1. One-time: CI publishing setup

In the GitHub repo, set two Actions secrets (Settings → Secrets and variables → Actions):

| Secret | Value |
| --- | --- |
| `DOCKERHUB_USERNAME` | Docker Hub username/namespace the images publish under |
| `DOCKERHUB_TOKEN` | Docker Hub access token with read/write scope |

Images publish automatically when:

- a commit lands on `main` (after `test-api` + `test-web` pass), or
- you run the **build-edgewise** workflow manually via *Actions → build-edgewise → Run workflow*.

Each publish pushes multi-arch (`linux/amd64`, `linux/arm64`) images tagged
`latest` **and** the commit SHA — use the SHA tag for pinned/rollback deploys.

## 2. Create the stack in Portainer

1. **Stacks → Add stack**, name it `edgewise`.
2. Either paste the contents of `docker-compose.yml`, or use *Repository* mode
   pointing at this repo (compose path: `docker-compose.yml`).
3. Fill in the environment variables (Portainer's *Environment variables*
   section, or upload a `.env` — see [.env.example](../.env.example)):

| Variable | Required | Generate with |
| --- | --- | --- |
| `POSTGRES_PASSWORD` | yes | `openssl rand -base64 24` |
| `EDGEWISE_JWT_SECRET` | yes | `openssl rand -base64 48` |
| `EDGEWISE_ENCRYPTION_KEY` | yes — exactly 32 bytes base64 | `openssl rand -base64 32` |
| `OPENROUTER_API_KEY` | optional (Coach — provider of choice) | openrouter.ai/keys |
| `ANTHROPIC_API_KEY` | optional (Coach fallback provider) | console.anthropic.com |
| `EDGEWISE_VAPID_PUBLIC_KEY` / `EDGEWISE_VAPID_PRIVATE_KEY` / `EDGEWISE_VAPID_SUBJECT` | optional (push) | `npx web-push generate-vapid-keys`; subject is `mailto:you@example.com` |
| `SMTP_HOST` / `SMTP_PORT` / `SMTP_USER` / `SMTP_PASSWORD` / `SMTP_FROM` | optional (email) | GoDaddy Workspace Email: `smtpout.secureserver.net:587` (STARTTLS) or `:465` (SSL), user = full mailbox address on your domain; GoDaddy M365 mailboxes: `smtp.office365.com:587` |
| `DOCKERHUB_USERNAME` | optional (default `frostaura`) | — |
| `EDGEWISE_TAG` | optional (default `latest`) | a commit SHA to pin |
| `EDGEWISE_PORT` | optional (default `8080`) | — |
| `TZ` | optional (default `Africa/Johannesburg`) | — |

4. **Deploy the stack.**

## 3. First boot

- The API waits for Postgres to be healthy, then **runs EF Core migrations
  automatically** (with built-in retry) — no manual schema step.
- Browse to `http://<host>:<EDGEWISE_PORT>` and register.
  **The first registered user becomes the admin.**
- Health: the web container answers on `/`; the API's `/health` is checked
  internally by Docker (and reachable via the web proxy at `/api/../health`
  only if exposed by the API — use container health status in Portainer).

## 4. Volumes and backups

| Volume | Contents |
| --- | --- |
| `edgewise-db` | Postgres data directory |
| `edgewise-data` | Attachment files (`/data` in the API container) |

Database backup (run on the host):

```bash
docker exec $(docker ps -qf name=edgewise-db) \
  pg_dump -U edgewise -d edgewise | gzip > edgewise-$(date +%F).sql.gz
```

Also back up the `edgewise-data` volume (e.g. `docker run --rm -v
edgewise-data:/data -v $PWD:/backup alpine tar czf /backup/edgewise-data.tgz /data`).
Restore order: restore volumes/db first, then start the stack.

## 5. Upgrading

1. Merge to `main` (or dispatch the workflow) and wait for the publish to finish.
2. In Portainer: open the stack → **Pull and redeploy** (enable *Re-pull image*).
   With `EDGEWISE_TAG=latest` this picks up the newest images; if pinned, change
   the tag to the new SHA first.
3. Migrations run automatically on API startup. To roll back, redeploy with the
   previous SHA tag (schema rollbacks are not automatic — restore from backup if
   a migration must be undone).

## 6. Reverse proxy and TLS

The stack publishes plain HTTP on `EDGEWISE_PORT`. Put it behind your existing
reverse proxy (Traefik, Caddy, nginx, Cloudflare Tunnel, …) and terminate TLS
there, forwarding to `http://<host>:<EDGEWISE_PORT>`.

**HTTPS is required for full functionality**: Edgewise is a PWA — service
worker installation and Web Push notifications only work over HTTPS (or
`localhost`). The web container's nginx already forwards `X-Forwarded-Proto`
and friends to the API, and SSE streams (`/api/coach/chat`) are proxied
unbuffered — make sure your outer proxy also disables response buffering for
that path and allows long-lived connections.
