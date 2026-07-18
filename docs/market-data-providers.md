# Market data providers

All external market data flows through provider chains in
`Edgewise.Infrastructure`. Each chain tries providers in order and **falls
back** on error, timeout, or rate-limit (Polly retry/circuit-breaker). If every
provider fails, the last cached value is served and explicitly marked
**stale** with its fetch timestamp — the UI must show staleness, never hide it.

## Chains

| Data | Chain (in order) | Fallback terminal |
| --- | --- | --- |
| **Equities** quotes/candles | Yahoo Finance → Stooq | cached (stale) |
| **Crypto** quotes/candles | Binance → Coinbase → CoinGecko | cached (stale) |
| **FX** rates | Frankfurter → ExchangeRate-API | cached (stale) |
| **Fear & Greed** | alternative.me | cached (stale) |
| **News** | RSS feeds + Finnhub | cached (stale) |
| **Economic calendar** | Finnhub | cached (stale) |

## Rate limits (working assumptions)

| Provider | Limit posture | Handling |
| --- | --- | --- |
| Yahoo Finance | unofficial; throttles bursts | batch symbols, jittered schedule, back off on 429 |
| Stooq | unofficial CSV; be polite | fallback only, low frequency |
| Binance | 1200 req-weight/min (public) | primary crypto; well within budget |
| Coinbase | ~10 req/s public | fallback only |
| CoinGecko | ~10–30 req/min (free) | last resort; coarse polling |
| Frankfurter | unmetered (ECB daily data) | primary FX; daily granularity |
| ExchangeRate-API | ~1.5k req/month (free) | fallback only |
| alternative.me | unmetered, updates daily | poll hourly at most |
| Finnhub | 60 req/min (free) | calendar/news budgeted centrally |

All polling runs through Hangfire jobs with centralized per-provider budgets —
user requests never hit providers directly; they read the cache.

## Closed-candles-immutable rule

Once a candle's period has closed and it is persisted, it is **never
updated** — later fetches may only *append* newer candles. Corrections from a
provider do not rewrite history (analytics, backtests, and adherence checks
must be reproducible). The only sanctioned rewrite is an explicit, logged
backfill migration.

Open (current-period) candles are volatile and are cached but not persisted as
history until closed.

## EODHD swap path

If/when an EODHD subscription is added (`EDGEWISE_EODHD_API_KEY`):

1. EODHD becomes the **primary** for equities quotes/candles and the economic
   calendar; Yahoo → Stooq shift down to fallbacks.
2. Provider chains are configuration-ordered — the swap is a config/DI change
   in `Edgewise.Infrastructure`, no domain changes.
3. Cached rows record their source provider, so mixed-source history remains
   traceable; the closed-candles-immutable rule still applies (no rewriting
   Yahoo-era candles with EODHD data).
