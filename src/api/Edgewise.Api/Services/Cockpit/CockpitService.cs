using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Cockpit;

/// <summary>
/// Computes the five cockpit reads server-side. Falls back to direct DbContext
/// reads (latest Snapshot, QuoteCache) where richer concurrently-built services
/// (PortfolioService, MarketDataService) are unavailable.
/// </summary>
public sealed class CockpitService(EdgewiseDbContext db)
{
    public static readonly string[] SelfStates = ["calm", "tired", "tilted", "rushed"];

    /// <summary>Drawdown ladder rung thresholds (% drawdown from HWM).</summary>
    private static readonly (decimal Threshold, string Rung)[] LadderRungs =
    [
        (5m, "full"),
        (10m, "half"),
        (15m, "quarter"),
        (decimal.MaxValue, "paused"),
    ];

    public async Task<CockpitStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(ct)
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        var tz = ResolveTimezone(user.Timezone);
        var nowUtc = DateTime.UtcNow;
        var (dayStartUtc, dayEndUtc) = TodayUtcRange(nowUtc, tz);

        var risk = await db.RiskProfiles.OrderByDescending(r => r.IsActive).ThenBy(r => r.Name)
            .FirstOrDefaultAsync(ct);
        var heatCapPct = risk?.HeatCapPct is > 0 ? risk.HeatCapPct : 4m;
        var dailyStopPct = risk?.DailyStopPct is > 0 ? risk.DailyStopPct : 3m;
        var lossCountStop = risk?.DailyLossCountStop is > 0 ? risk.DailyLossCountStop : 3;

        var tradingBucket = await db.Buckets.FirstOrDefaultAsync(b => b.Kind == BucketKind.Trading, ct);
        var equityMinor = await LatestEquityMinorAsync(tradingBucket?.Id, ct);

        var heat = await ComputeHeatAsync(equityMinor, heatCapPct, ct);
        var daily = await ComputeDailyPnlAsync(dayStartUtc, dayEndUtc, equityMinor, dailyStopPct, lossCountStop, ct);
        var ladder = ComputeLadder(tradingBucket, equityMinor);
        var calendar = await ComputeCalendarAsync(nowUtc, ct);
        var state = ComputeSelfState(user, tz, dayStartUtc, dayEndUtc);

        var activeOverride = await db.OverrideLogs
            .Where(o => o.Kind == OverrideKind.CockpitRed && o.At >= dayStartUtc && o.At < dayEndUtc)
            .OrderByDescending(o => o.At)
            .Select(o => new OverrideDto(o.Id, nameof(OverrideKind.CockpitRed), o.Reason, o.At))
            .FirstOrDefaultAsync(ct);

        var statuses = new[] { heat.Status, daily.Status, ladder.Status, calendar.Status, state.Status };
        var allGreen = statuses.All(s => s == ReadStatus.Green);
        var anyRed = statuses.Any(s => s == ReadStatus.Red);
        var newPlanUnlocked = !anyRed || activeOverride is not null;

        var today = new TodayStripDto(
            daily.RealisedR,
            daily.PnlMinor,
            await db.Trades.CountAsync(
                t => !t.IsPaper && t.ClosedAt != null && t.ClosedAt >= dayStartUtc && t.ClosedAt < dayEndUtc, ct),
            daily.ProgressPct);

        return new CockpitStatusDto(
            new CockpitReadsDto(heat, daily, ladder, calendar, state),
            allGreen,
            newPlanUnlocked,
            activeOverride,
            today);
    }

    /// <summary>Persists today's self-declared state into User.SettingsJson (key "cockpitState").</summary>
    public async Task SetStateAsync(string state, CancellationToken ct)
    {
        state = state.Trim().ToLowerInvariant();
        if (!SelfStates.Contains(state))
        {
            throw ApiException.BadRequest("invalid_state", $"state must be one of: {string.Join(", ", SelfStates)}.");
        }

        var user = await db.Users.SingleOrDefaultAsync(ct)
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        var settings = ParseSettings(user.SettingsJson);
        settings["cockpitState"] = JsonSerializer.SerializeToElement(
            new Dictionary<string, object> { ["state"] = state, ["at"] = DateTime.UtcNow });
        user.SettingsJson = JsonSerializer.Serialize(settings);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Logs a CockpitRed override (unlocks planning for the rest of the day). When the
    /// daily circuit breaker is currently tripped, a CircuitBreaker override row is
    /// logged too so the adherence engine can penalise trades closed today.
    /// </summary>
    public async Task<CockpitStatusDto> OverrideAsync(string reason, CancellationToken ct)
    {
        reason = reason.Trim();
        if (reason.Length < 3)
        {
            throw ApiException.BadRequest("reason_required", "An override reason of at least 3 characters is required.");
        }

        var user = await db.Users.SingleOrDefaultAsync(ct)
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        var statusBefore = await GetStatusAsync(ct);
        var now = DateTime.UtcNow;

        db.OverrideLogs.Add(new OverrideLog
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Kind = OverrideKind.CockpitRed,
            Reason = reason,
            At = now,
        });

        if (statusBefore.Reads.DailyPnl.Tripped)
        {
            db.OverrideLogs.Add(new OverrideLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Kind = OverrideKind.CircuitBreaker,
                Reason = reason,
                At = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return await GetStatusAsync(ct);
    }

    /// <summary>Catalyst calendar for Radar: events within the next <paramref name="days"/> days.</summary>
    public async Task<IReadOnlyList<CatalystItemDto>> GetCatalystsAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 30);
        var nowUtc = DateTime.UtcNow;
        var horizon = nowUtc.AddDays(days);

        var (held, watched) = await HeldAndWatchedAsync(ct);
        var events = await db.CatalystEvents
            .Where(c => c.At >= nowUtc && c.At < horizon)
            .OrderBy(c => c.At)
            .ToListAsync(ct);

        var symbols = await SymbolsAsync(events.Where(e => e.InstrumentId != null).Select(e => e.InstrumentId!.Value), ct);

        return events
            .Select(e => ToCatalystDto(e, symbols, held, watched))
            .ToList();
    }

    // ------------------------------------------------------------- the reads

    private async Task<HeatReadDto> ComputeHeatAsync(long equityMinor, decimal capPct, CancellationToken ct)
    {
        var openTrades = await db.Trades.Where(t => t.Status == TradeStatus.Open && !t.IsPaper).ToListAsync(ct);
        var planIds = openTrades.Where(t => t.PlanId != null).Select(t => t.PlanId!.Value).ToList();
        var stops = await db.TradePlans
            .Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.StopPrice, ct);

        var openRisk = 0m;
        var estimated = false;
        foreach (var trade in openTrades)
        {
            if (trade.PlanId != null && stops.TryGetValue(trade.PlanId.Value, out var stop) && stop > 0)
            {
                openRisk += Math.Abs(trade.AvgEntryPrice - stop) * trade.Qty;
            }
            else
            {
                // Unplanned trade: estimate 1R as 1% of entry notional.
                openRisk += Math.Abs(trade.AvgEntryPrice * 0.01m) * trade.Qty;
                estimated = true;
            }
        }

        if (openTrades.Count == 0)
        {
            return new HeatReadDto(0, 0, capPct, false, 0, ReadStatus.Green, "No open positions — no heat.");
        }

        if (equityMinor <= 0)
        {
            return new HeatReadDto(
                Math.Round(openRisk, 2), null, capPct, estimated, openTrades.Count, ReadStatus.Amber,
                "Open risk exists but there is no equity snapshot to compare against.");
        }

        var equityMajor = equityMinor / 100m;
        var heatPct = Math.Round(openRisk / equityMajor * 100m, 2);
        var status = heatPct > capPct ? ReadStatus.Red
            : heatPct >= capPct * 0.8m ? ReadStatus.Amber
            : ReadStatus.Green;
        var detail = status switch
        {
            ReadStatus.Red => $"Heat {heatPct}% is over your {capPct}% cap.",
            ReadStatus.Amber => $"Heat {heatPct}% is near your {capPct}% cap.",
            _ => $"Heat {heatPct}% of equity, cap {capPct}%.",
        };
        if (estimated)
        {
            detail += " Includes 1R estimates for unplanned trades.";
        }

        return new HeatReadDto(Math.Round(openRisk, 2), heatPct, capPct, estimated, openTrades.Count, status, detail);
    }

    private async Task<DailyPnlReadDto> ComputeDailyPnlAsync(
        DateTime dayStartUtc, DateTime dayEndUtc, long equityMinor, decimal dailyStopPct, int lossCountStop,
        CancellationToken ct)
    {
        var closedToday = await db.Trades
            .Where(t => !t.IsPaper && t.ClosedAt != null && t.ClosedAt >= dayStartUtc && t.ClosedAt < dayEndUtc)
            .ToListAsync(ct);

        var pnlMinor = closedToday.Sum(t => t.RealisedPnlMinor);
        var lossCount = closedToday.Count(t => t.RealisedPnlMinor < 0);
        var realisedR = Math.Round(closedToday.Sum(t => t.RRealised ?? 0m), 2);
        var stopMinor = (long)Math.Round(equityMinor * dailyStopPct / 100m);

        var progressPct = stopMinor > 0
            ? Math.Clamp(Math.Round(-pnlMinor * 100m / stopMinor, 1), 0m, 100m)
            : 0m;

        var stopHit = stopMinor > 0 && pnlMinor <= -stopMinor;
        var countHit = lossCountStop > 0 && lossCount >= lossCountStop;
        var tripped = stopHit || countHit;

        var status = tripped ? ReadStatus.Red
            : progressPct >= 60m || (lossCountStop > 1 && lossCount >= lossCountStop - 1) ? ReadStatus.Amber
            : ReadStatus.Green;

        var detail = tripped
            ? stopHit
                ? $"Circuit breaker: daily stop hit ({FormatMinor(pnlMinor)} vs -{FormatMinor(stopMinor)} limit)."
                : $"Circuit breaker: {lossCount} losses today (limit {lossCountStop})."
            : status == ReadStatus.Amber
                ? $"Approaching daily stop: {FormatMinor(pnlMinor)} today, {lossCount} losses."
                : closedToday.Count == 0
                    ? "No closed trades today."
                    : $"{FormatMinor(pnlMinor)} realised over {closedToday.Count} trades, {lossCount} losses.";

        return new DailyPnlReadDto(
            pnlMinor, realisedR, lossCount, lossCountStop, stopMinor, progressPct, tripped, status, detail);
    }

    private static LadderReadDto ComputeLadder(Bucket? tradingBucket, long equityMinor)
    {
        var hwmMinor = tradingBucket?.HighWaterMarkMinor ?? 0;
        if (hwmMinor <= 0 || equityMinor <= 0)
        {
            return new LadderReadDto(0, "full", 5m, false, ReadStatus.Green,
                "Full size — no drawdown recorded against the Trading bucket high-water mark.");
        }

        var drawdownPct = Math.Round(Math.Max(0m, (hwmMinor - equityMinor) * 100m / hwmMinor), 2);
        var rung = LadderRungs.First(r => drawdownPct < r.Threshold).Rung;
        decimal? nextRung = rung switch
        {
            "full" => 5m,
            "half" => 10m,
            "quarter" => 15m,
            _ => null,
        };
        var locked = rung == "paused";
        var status = locked ? ReadStatus.Red : rung == "full" ? ReadStatus.Green : ReadStatus.Amber;
        var detail = locked
            ? $"Paused: {drawdownPct}% drawdown from high-water mark (>= 15%). New plans are locked."
            : nextRung is { } next
                ? $"{Capitalise(rung)} size at {drawdownPct}% drawdown; next rung at {next}%."
                : $"{Capitalise(rung)} size at {drawdownPct}% drawdown.";

        return new LadderReadDto(drawdownPct, rung, nextRung, locked, status, detail);
    }

    private async Task<CalendarReadDto> ComputeCalendarAsync(DateTime nowUtc, CancellationToken ct)
    {
        var (held, watched) = await HeldAndWatchedAsync(ct);
        var relevant = held.Union(watched).ToHashSet();
        var horizon = nowUtc.AddHours(24);

        var events = await db.CatalystEvents
            .Where(c => c.At >= nowUtc && c.At < horizon)
            .OrderBy(c => c.At)
            .ToListAsync(ct);

        // Touching: instrument held/watched, or an instrument-less (macro/user) event.
        var touching = events
            .Where(e => e.InstrumentId == null || relevant.Contains(e.InstrumentId.Value))
            .ToList();

        var symbols = await SymbolsAsync(
            touching.Where(e => e.InstrumentId != null).Select(e => e.InstrumentId!.Value), ct);
        var items = touching.Select(e => ToCatalystDto(e, symbols, held, watched)).ToList();

        var redCount = items.Count(i => i.Severity == "red");
        var status = redCount > 0 ? ReadStatus.Amber : ReadStatus.Green;
        var detail = redCount > 0
            ? $"{redCount} red catalyst{(redCount == 1 ? "" : "s")} inside the next 24h touching held/watched instruments."
            : items.Count > 0
                ? $"{items.Count} catalyst{(items.Count == 1 ? "" : "s")} in the next 24h, none red."
                : "No catalysts in the next 24h.";

        return new CalendarReadDto(items, status, detail);
    }

    private static SelfStateReadDto ComputeSelfState(User user, TimeZoneInfo tz, DateTime dayStartUtc, DateTime dayEndUtc)
    {
        _ = tz;
        var settings = ParseSettings(user.SettingsJson);
        if (settings.TryGetValue("cockpitState", out var element)
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("state", out var stateEl)
            && element.TryGetProperty("at", out var atEl)
            && stateEl.ValueKind == JsonValueKind.String
            && atEl.TryGetDateTime(out var at))
        {
            var atUtc = at.Kind == DateTimeKind.Utc ? at : at.ToUniversalTime();
            var state = stateEl.GetString()!;
            if (atUtc >= dayStartUtc && atUtc < dayEndUtc && SelfStates.Contains(state))
            {
                var amber = state is "tilted" or "rushed" or "tired";
                return new SelfStateReadDto(
                    state,
                    atUtc,
                    amber ? ReadStatus.Amber : ReadStatus.Green,
                    amber
                        ? $"You declared you're {state}. Consider standing down or sizing down."
                        : "You declared you're calm.");
            }
        }

        return new SelfStateReadDto(null, null, ReadStatus.Green, "No state declared today.");
    }

    // --------------------------------------------------------------- helpers

    private async Task<long> LatestEquityMinorAsync(Guid? tradingBucketId, CancellationToken ct)
    {
        if (tradingBucketId is { } bucketId)
        {
            var bucketSnapshot = await db.Snapshots
                .Where(s => s.BucketId == bucketId)
                .OrderByDescending(s => s.Date)
                .FirstOrDefaultAsync(ct);
            if (bucketSnapshot is not null)
            {
                return bucketSnapshot.EquityMinor;
            }
        }

        var overall = await db.Snapshots
            .Where(s => s.BucketId == null)
            .OrderByDescending(s => s.Date)
            .FirstOrDefaultAsync(ct);
        return overall?.EquityMinor ?? 0;
    }

    private async Task<(HashSet<Guid> Held, HashSet<Guid> Watched)> HeldAndWatchedAsync(CancellationToken ct)
    {
        var held = (await db.Holdings.Select(h => h.InstrumentId).ToListAsync(ct))
            .Concat(await db.Trades.Where(t => t.Status == TradeStatus.Open).Select(t => t.InstrumentId).ToListAsync(ct))
            .ToHashSet();
        var watched = (await db.WatchlistItems.Select(w => w.InstrumentId).ToListAsync(ct)).ToHashSet();
        return (held, watched);
    }

    private async Task<Dictionary<Guid, string>> SymbolsAsync(IEnumerable<Guid> instrumentIds, CancellationToken ct)
    {
        var ids = instrumentIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Instruments.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Symbol, ct);
    }

    private static CatalystItemDto ToCatalystDto(
        CatalystEvent e, Dictionary<Guid, string> symbols, HashSet<Guid> held, HashSet<Guid> watched) =>
        new(
            e.Id,
            Camel(e.Kind.ToString()),
            e.Severity == CatalystSeverity.Red ? "red" : "amber",
            e.Title,
            e.At,
            e.InstrumentId,
            e.InstrumentId is { } id && symbols.TryGetValue(id, out var symbol) ? symbol : null,
            e.InstrumentId is { } heldId && held.Contains(heldId),
            e.InstrumentId is { } watchedId && watched.Contains(watchedId));

    private static Dictionary<string, JsonElement> ParseSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static TimeZoneInfo ResolveTimezone(string? timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    internal static (DateTime StartUtc, DateTime EndUtc) TodayUtcRange(DateTime nowUtc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var startLocal = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);
        return (startUtc, startUtc.AddDays(1));
    }

    private static string FormatMinor(long minor) => (minor / 100m).ToString("+0.00;-0.00;0.00");

    private static string Capitalise(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static string Camel(string value) => char.ToLowerInvariant(value[0]) + value[1..];
}
