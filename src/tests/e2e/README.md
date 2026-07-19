# Edgewise end-to-end suite

Playwright smoke suite that drives the real stack — React SPA + .NET API +
PostgreSQL — through one continuous user journey, then captures the visual QA
screenshot set committed under `screenshots/`.

## Running locally

The suite expects a running stack and a **fresh database** (registration starts
clean and the visual project reuses the journey's data).

```bash
# 1. Fresh database
psql -h localhost -U edgewise -d postgres \
  -c "DROP DATABASE IF EXISTS edgewise_e2e WITH (FORCE);" \
  -c "CREATE DATABASE edgewise_e2e;"

# 2. API on :5210 (migrates + seeds on boot)
ASPNETCORE_ENVIRONMENT=Development \
DisableHangfire=true \
ConnectionStrings__Default="Host=localhost;Database=edgewise_e2e;Username=edgewise;Password=edgewise" \
dotnet run --project src/api/Edgewise.Api --urls http://localhost:5210
# wait for GET /health -> 200

# 3. Web dev server on :5173 (proxies /api -> :5210)
cd src/web && npm run dev

# 4. The suite
cd src/tests/e2e
npm install
npx playwright test
```

Environment knobs:

- `EDGEWISE_WEB_URL` (default `http://localhost:5173`)
- `EDGEWISE_API_URL` (default `http://localhost:5210`, used by the visual
  project for per-context API logins)
- `EDGEWISE_CHROMIUM` — Chromium binary path. Defaults to
  `/opt/pw-browsers/chromium` when that file exists (pre-provisioned CI
  sandbox); otherwise Playwright's own browser resolution is used.

Full run from a clean database: **~85 s** (journey ~25 s + 38 screenshots).

## Projects

- **journey** (`specs/journey.spec.ts`) — one serial worker, state accumulates
  across tests on a shared page:
  1. Register a unique account → lands authenticated on the Cockpit.
  2. Cockpit shows the five reads (Heat, Daily P&L, Ladder, Calendar, State)
     and an unlocked plan gate.
  3. Settings → Risk shows the seeded profiles (Standard active,
     Conservative, Aggressive).
  4. New Plan: Breakout-Retest template prefill, BTC-USD instrument via
     combobox, entry/stop/trigger/target/invalidation/size, live size preview
     responds (fresh bucket equity is 0 → suggested qty 0, explicit size
     typed), full checklist confirm, submit → toast + redirect to journal.
  5. Quick Log an unplanned fill (BTC-USD buy 0.1 @ 50000, fee 5) → confess →
     auto-created trade, lands on trade detail.
  6. Trade detail (open): plan-vs-execution panel shows the unplanned-trade
     explainer; adherence pending ("Scored when the trade closes.").
  7. Close trade @ 51000 → adherence scored, `NO_PLAN` deduction evidence
     visible.
  8. Journal list shows the trade with an adherence chip capped at grade C or
     lower (unplanned cap).
  9. Portfolio renders (empty state).
  10. Forecast Studio: manual assumption row (BTC), contribution 12000/yr at
      100% split, deterministic run → bands SVG renders.
  11. Radar: create watchlist, add ETH-USD with a note, open the alert
      manager dialog.
  12. Lab: create a strategy from a template → builder condition rows visible
      → add a condition → save (new version).
  13. Coach chat: sending a message surfaces the offline card (no LLM key)
      with the "Open journal analytics" link.
  14. All seven settings sections render.
  15. Dark-mode toggle flips `<html class="dark">` and persists across reload.
  16. Logout → login roundtrip.

  Every step asserts zero unexpected `console.error` / uncaught page errors
  (see noise policy below).

- **visual** (`specs/visual.spec.ts`, depends on journey) — screenshots at
  1440×900 (desktop, full page) and 390×844 (mobile, viewport so tab-bar
  overlap is visible). Because refresh tokens rotate on use, each capture
  context performs its own API login with the journey's credentials
  (stored in `.auth/run-state.json`, gitignored) and seeds
  `localStorage` the way the SPA expects.

## Known-acceptable console noise

Filtered in `specs/helpers.ts` (`IGNORED_CONSOLE_PATTERNS`):

- `Failed to load resource … 401/403/404` — optional/defensive endpoints
  (e.g. a not-yet-run backtest id, requests raced by logout). The UI handles
  these 4xx responses and degrades gracefully.
- React DevTools advertisement and `[vite]` HMR chatter (dev server only).

Anything else with severity `error` fails the journey at the next
`assertClean` checkpoint.

## Screenshot index (`screenshots/`)

Grid: every screen at `desktop-light` + `mobile-light`; representative
screens additionally at `desktop-dark` (and cockpit/login at `mobile-dark`).

| Screen | Files |
| --- | --- |
| Cockpit | `cockpit-{desktop,mobile}-{light,dark}` |
| Journal list (with trade) | `journal-desktop-{light,dark}`, `journal-mobile-light` |
| Trade detail (closed, NO_PLAN) | `trade-detail-desktop-{light,dark}`, `trade-detail-mobile-light` |
| New plan | `new-plan-{desktop,mobile}-light` |
| Inbox (empty) | `inbox-{desktop,mobile}-light` |
| Import | `import-{desktop,mobile}-light` |
| Portfolio (empty) | `portfolio-{desktop,mobile}-light` |
| Forecast (bands rendered) | `forecast-desktop-{light,dark}`, `forecast-mobile-light` |
| Radar (watchlist + item) | `radar-{desktop,mobile}-light` |
| Lab builder | `lab-builder-{desktop,mobile}-light` |
| Backtest report (not-found state) | `backtest-report-{desktop,mobile}-light` |
| Coach chat | `coach-chat-{desktop,mobile}-light` |
| Settings → Risk | `settings-risk-desktop-{light,dark}`, `settings-risk-mobile-light` |
| Settings → Security | `settings-security-{desktop,mobile}-light` |
| Login | `login-{desktop,mobile}-{light,dark}` |

## PWA sanity (manual, optional)

`npm run build && npm run preview` in `src/web`, then:
`/manifest.webmanifest` responds 200 `application/manifest+json`, `/sw.js`
responds 200, and `navigator.serviceWorker.ready` resolves with an active
worker on page load. (The vite dev server does not register the SW.)
