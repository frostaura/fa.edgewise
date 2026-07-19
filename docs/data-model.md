# Data model reference

Entity reference grouped by pillar. All entities have `Id` (GUID), `CreatedAt`,
`UpdatedAt`; user-owned entities carry `UserId`. Persistence is EF Core +
PostgreSQL; migrations run automatically at API startup. *(Reference document —
the source of truth is the entity configuration in `Edgewise.Infrastructure`.)*

## Core / Auth

| Entity | Purpose | Key fields / uniques |
| --- | --- | --- |
| `User` | Account; first registered user is admin | `Email` (unique), `PasswordHash` (BCrypt), `IsAdmin`, `TotpSecret?` (encrypted) |
| `RefreshToken` | JWT refresh session | `TokenHash` (unique), `ExpiresAt`, `RevokedAt?` |
| `PushSubscription` | Web Push endpoint per device | `Endpoint` (unique per user), `P256dh`, `Auth` |
| `UserSettings` | Per-user config: heat cap, circuit-breaker rules, TZ | unique per `UserId` |

## Journal

| Entity | Purpose | Key fields / uniques |
| --- | --- | --- |
| `TradePlan` | Pre-entry plan a trade is graded against | `Symbol`, `Direction`, `Trigger`, `MaxSize`, `Stop`, `Target`, `Thesis`, `CreatedAt` (must precede entry) |
| `Trade` | Executed trade (may link to a plan) | `PlanId?`, `Symbol`, `Direction`, `EntryAt`, `ExitAt?`, `EntryPrice`, `ExitPrice?`, `Size`, `Fees`, `Pnl?` |
| `TradeExecution` | Individual fills composing a trade | `TradeId`, `At`, `Price`, `Quantity`, `Side` |
| `AdherenceScore` | Rubric result for a closed trade | `TradeId` (unique), `RubricVersion`, `Score`, `Grade`, `Deductions` (jsonb) |
| `JournalEntry` | Free-form note/review (daily or per-trade) | `TradeId?`, `Date`, `Body` (markdown), `Mood?` |
| `Attachment` | File stored under `/data` | `Path`, `FileName`, `ContentType`, `Size`; linked to trade/entry |
| `Tag` / `TradeTag` | Setup and mistake tagging | `Name` (unique per user, per kind) |

## Coach

| Entity | Purpose | Key fields / uniques |
| --- | --- | --- |
| `CoachSession` | One conversation with the coach | `Title`, `StartedAt` |
| `CoachMessage` | Message in a session | `SessionId`, `Role`, `Content`, `GroundingRefs` (jsonb), `ValidatorVerdict` |

## Portfolio

| Entity | Purpose | Key fields / uniques |
| --- | --- | --- |
| `Account` | Broker/exchange account bucket | `Name` (unique per user), `Currency`, `StartingBalance` |
| `Position` | Open position derived from trades | `AccountId`, `Symbol` (unique per account), `Quantity`, `AvgPrice` |
| `EquitySnapshot` | Daily equity/heat rollup (Hangfire) | `AccountId`, `Date` (unique per account), `Equity`, `Heat` |
| `CircuitBreakerEvent` | Trip/override audit trail | `TrippedAt`, `Rule`, `OverriddenAt?` |

## Radar

| Entity | Purpose | Key fields / uniques |
| --- | --- | --- |
| `Watchlist` / `WatchlistItem` | Symbol lists | `Name` unique per user; `Symbol` unique per watchlist |
| `Alert` | Price/news alert rule | `Symbol`, `Kind`, `Threshold`, `TriggeredAt?`, `Channel` (push/email) |
| `CalendarEvent` | Economic calendar (Finnhub sync) | `ExternalId` (unique), `At`, `Impact` (red/amber/green), `Country` |
| `NewsItem` | Synced news/RSS | `ExternalId`/`Url` (unique), `PublishedAt`, `Source`, `Sentiment?` |
| `SentimentReading` | Fear & Greed history | `Source`, `At` (unique per source), `Value` |

## Lab

| Entity | Purpose | Key fields / uniques |
| --- | --- | --- |
| `Experiment` | A hypothesis being tracked ("A+ setups only for 30 days") | `Name`, `StartAt`, `EndAt?`, `Criteria` (jsonb), `Status` |
| `ExperimentTrade` | Trades enrolled in an experiment | `ExperimentId` + `TradeId` (unique pair) |

## Market data (shared cache)

| Entity | Purpose | Key fields / uniques |
| --- | --- | --- |
| `Quote` | Latest price per symbol | `Symbol` (unique), `Price`, `AsOf`, `Provider`, `IsStale` |
| `Candle` | Historical OHLCV — **immutable once closed** | `Symbol` + `Interval` + `OpenTime` (unique), `Provider` |
| `FxRate` | Cached FX rates | `Base` + `Quote` + `Date` (unique) |
