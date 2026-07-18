using System.Text.Json;
using Edgewise.Domain.Engines.Adherence;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using DomainEntities = Edgewise.Domain.Entities;

namespace Edgewise.Api.Services.Journal;

/// <summary>
/// Assembles an <see cref="AdherenceInput"/> from journal data and persists the score.
///
/// Documented approximations (best-effort by design):
///  - Bucket equity is the latest snapshot / cost basis "as of now", not reconstructed
///    at the moment of entry.
///  - Open-risk heat at entry sums |entry - plan stop| x qty over the user's OTHER trades
///    that were open at this trade's entry and have a plan; unplanned open trades
///    contribute nothing.
///  - Prior-loss lookup considers the latest closed losing trade in the same asset class
///    that closed before this trade's entry.
///  - Circuit-breaker override = any OverrideLog row of kind CircuitBreaker on the same
///    UTC day as the entry.
///  - Red-event exposure = any red CatalystEvent for the instrument (or global) whose
///    timestamp falls inside the holding window.
///  - Engine prices are minor units (x100) so qty x price-distance compares to equity.
/// </summary>
public sealed class AdherencePipeline(EdgewiseDbContext db, BucketEquityService bucketEquity)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Scores the (closed) trade and appends a new AdherenceResult row. Returns the persisted row.</summary>
    public async Task<DomainEntities.AdherenceResult> ComputeAndPersistAsync(
        DomainEntities.Trade trade, ExitRuleOutcome exitRule, CancellationToken ct)
    {
        var input = await AssembleInputAsync(trade, exitRule, ct);
        var score = AdherenceEngine.Score(input);

        var result = new DomainEntities.AdherenceResult
        {
            Id = Guid.NewGuid(),
            TradeId = trade.Id,
            Trade = trade,
            RubricVersion = 1,
            Score = score.Score,
            Grade = score.Grade,
            DeductionsJson = JsonSerializer.Serialize(score.Deductions, Json),
            ComputedAt = DateTime.UtcNow,
        };
        db.AdherenceResults.Add(result);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<AdherenceInput> AssembleInputAsync(
        DomainEntities.Trade trade, ExitRuleOutcome exitRule, CancellationToken ct)
    {
        var plan = trade.PlanId is Guid planId
            ? await db.TradePlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct)
            : null;

        var fills = await db.Fills.AsNoTracking()
            .Where(f => f.TradeId == trade.Id)
            .OrderBy(f => f.At)
            .ToListAsync(ct);

        var entrySide = trade.Direction == DomainEntities.TradeDirection.Long
            ? DomainEntities.FillSide.Buy
            : DomainEntities.FillSide.Sell;

        // Fills in minor-unit prices; fall back to the trade's aggregates when no fill rows exist.
        var entryFills = fills.Where(f => f.Side == entrySide)
            .Select(f => new TradeFill(f.Qty, f.Price * 100m, f.At)).ToList();
        var exitFills = fills.Where(f => f.Side != entrySide)
            .Select(f => new TradeFill(f.Qty, f.Price * 100m, f.At)).ToList();
        if (entryFills.Count == 0)
        {
            entryFills = [new TradeFill(trade.Qty, trade.AvgEntryPrice * 100m, trade.OpenedAt)];
        }

        if (exitFills.Count == 0 && trade.AvgExitPrice is decimal exit && trade.ClosedAt is DateTime closedAt)
        {
            exitFills = [new TradeFill(trade.Qty, exit * 100m, closedAt)];
        }

        var firstFillAt = entryFills.Min(f => f.At);

        // Stop history from plan versions (FieldsJson snapshots), prices to minor units.
        var stopVersions = new List<StopVersion>();
        var plannedStopMinor = 0m;
        if (plan is not null)
        {
            var versions = await db.TradePlanVersions.AsNoTracking()
                .Where(v => v.PlanId == plan.Id)
                .OrderBy(v => v.Version)
                .ToListAsync(ct);
            foreach (var version in versions)
            {
                if (TryReadStop(version.FieldsJson) is decimal stop)
                {
                    stopVersions.Add(new StopVersion(stop * 100m, version.At));
                }
            }

            plannedStopMinor = (stopVersions.Count > 0 ? stopVersions[0].Stop : plan.StopPrice * 100m);
        }

        // Risk profile parameters (percent points in the DB, fractions for the engine).
        var profile = plan is not null
            ? await db.RiskProfiles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == plan.RiskProfileId, ct)
            : await db.RiskProfiles.AsNoTracking().OrderByDescending(r => r.IsActive).FirstOrDefaultAsync(ct);
        var riskPct = (profile?.RiskPct ?? 1m) / 100m;
        var heatCapPct = (profile?.HeatCapPct ?? 100m) / 100m;

        var equityMinor = await bucketEquity.GetEquityMinorAsync(trade.BucketId, ct);

        // Max position quantity: running sum over fills, falling back to trade qty.
        var maxQty = trade.Qty;
        if (fills.Count > 0)
        {
            var running = 0m;
            maxQty = 0m;
            foreach (var fill in fills)
            {
                running += fill.Side == entrySide ? fill.Qty : -fill.Qty;
                maxQty = Math.Max(maxQty, running);
            }

            if (maxQty <= 0m)
            {
                maxQty = trade.Qty;
            }
        }

        // Open-risk heat at entry (approximation, see class docs).
        var openRiskPct = await ComputeOpenRiskAtEntryAsync(trade, equityMinor, ct);

        // Prior same-class loss.
        int? minutesSincePriorLoss = null;
        var assetClass = await db.Instruments.AsNoTracking()
            .Where(i => i.Id == trade.InstrumentId)
            .Select(i => (DomainEntities.AssetClass?)i.AssetClass)
            .FirstOrDefaultAsync(ct);
        if (assetClass is not null)
        {
            var sameClassInstruments = await db.Instruments.AsNoTracking()
                .Where(i => i.AssetClass == assetClass)
                .Select(i => i.Id)
                .ToListAsync(ct);
            var priorLossClose = await db.Trades.AsNoTracking()
                .Where(t => t.Id != trade.Id
                    && t.Status == DomainEntities.TradeStatus.Closed
                    && t.RealisedPnlMinor < 0
                    && t.ClosedAt != null
                    && t.ClosedAt <= firstFillAt
                    && sameClassInstruments.Contains(t.InstrumentId))
                .OrderByDescending(t => t.ClosedAt)
                .Select(t => t.ClosedAt)
                .FirstOrDefaultAsync(ct);
            if (priorLossClose is DateTime lossAt)
            {
                minutesSincePriorLoss = (int)(firstFillAt - lossAt).TotalMinutes;
            }
        }

        // Red catalyst events inside the holding window.
        var windowEnd = trade.ClosedAt ?? DateTime.UtcNow;
        var tradedThroughRed = await db.CatalystEvents.AsNoTracking()
            .AnyAsync(c => c.Severity == DomainEntities.CatalystSeverity.Red
                && (c.InstrumentId == null || c.InstrumentId == trade.InstrumentId)
                && c.At >= firstFillAt && c.At <= windowEnd, ct);

        // Circuit-breaker override on the entry day.
        var dayStart = DateTime.SpecifyKind(firstFillAt.Date, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);
        var cbOverride = await db.OverrideLogs.AsNoTracking()
            .AnyAsync(o => o.Kind == DomainEntities.OverrideKind.CircuitBreaker
                && o.At >= dayStart && o.At < dayEnd, ct);

        return new AdherenceInput
        {
            PlanCreatedAt = plan?.CreatedAt,
            FirstFillAt = firstFillAt,
            StopVersions = stopVersions,
            Direction = trade.Direction == DomainEntities.TradeDirection.Long
                ? TradeDirection.Long
                : TradeDirection.Short,
            EntryFills = entryFills,
            ExitFills = exitFills,
            PlannedStopPrice = plannedStopMinor,
            PlanSizeQty = plan?.SizeQty ?? 0m,
            ActualMaxPositionQty = maxQty,
            BucketEquityMinor = equityMinor,
            RiskPct = riskPct,
            HeatCapPct = heatCapPct,
            OpenRiskAtEntryPct = openRiskPct,
            TriggerChecklistConfirmed = ChecklistConfirmed(plan),
            ExitRule = exitRule,
            MinutesSincePriorLossSameClass = minutesSincePriorLoss,
            TradedThroughRedEvent = tradedThroughRed,
            CircuitBreakerOverride = cbOverride,
        };
    }

    private async Task<decimal> ComputeOpenRiskAtEntryAsync(
        DomainEntities.Trade trade, long equityMinor, CancellationToken ct)
    {
        if (equityMinor <= 0)
        {
            return 0m;
        }

        var others = await db.Trades.AsNoTracking()
            .Where(t => t.Id != trade.Id
                && t.PlanId != null
                && t.OpenedAt <= trade.OpenedAt
                && (t.Status == DomainEntities.TradeStatus.Open
                    || (t.ClosedAt != null && t.ClosedAt > trade.OpenedAt)))
            .Select(t => new { t.Qty, t.AvgEntryPrice, t.PlanId })
            .ToListAsync(ct);
        if (others.Count == 0)
        {
            return 0m;
        }

        var planIds = others.Select(t => t.PlanId!.Value).Distinct().ToList();
        var stops = await db.TradePlans.AsNoTracking()
            .Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.StopPrice, ct);

        var totalRiskMinor = 0m;
        foreach (var other in others)
        {
            if (stops.TryGetValue(other.PlanId!.Value, out var stop))
            {
                totalRiskMinor += Math.Abs(other.AvgEntryPrice - stop) * other.Qty * 100m;
            }
        }

        return totalRiskMinor / equityMinor;
    }

    private static bool ChecklistConfirmed(DomainEntities.TradePlan? plan)
    {
        if (plan is null)
        {
            // No plan means no checklist to confirm; NO_PLAN already penalises this,
            // so the self-report rule does not double-fire.
            return true;
        }

        if (string.IsNullOrWhiteSpace(plan.ChecklistConfirmedJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(plan.ChecklistConfirmedJson);
            return doc.RootElement.ValueKind switch
            {
                // {"htf-trend": true, ...} — confirmed when every entry is true.
                JsonValueKind.Object => doc.RootElement.EnumerateObject()
                    .All(p => p.Value.ValueKind == JsonValueKind.True),
                // ["htf-trend", ...] — confirmed when non-empty.
                JsonValueKind.Array => doc.RootElement.GetArrayLength() > 0,
                JsonValueKind.True => true,
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static decimal? TryReadStop(string fieldsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(fieldsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("stopPrice", out var stop)
                && stop.TryGetDecimal(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed snapshots.
        }

        return null;
    }
}
