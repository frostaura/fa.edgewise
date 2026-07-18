using System.Text.Json;
using Edgewise.Domain.Engines.Performance;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Jobs.Portfolio;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Edgewise.Api.Services.Portfolio;

/// <summary>
/// User-scoped portfolio read model: valuations, summary, ladder state machine,
/// ratchet, performance (TWR/XIRR), allocation and net-worth series. The heavy
/// lifting (quotes, FX, per-holding valuation) is shared with the snapshot job via
/// <see cref="PortfolioValuationService"/>. Other verticals (e.g. the Cockpit) can
/// consume <see cref="GetLadderStateAsync"/> and <see cref="GetSummaryAsync"/>.
/// </summary>
public sealed class PortfolioService(
    EdgewiseDbContext db,
    ICurrentUser currentUser,
    PortfolioValuationService valuation)
{
    public static readonly LadderThresholdsDto DefaultLadderThresholds = new(0.05m, 0.10m, 0.15m);

    private Guid RequireUserId() =>
        currentUser.UserId ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

    // ------------------------------------------------------------- valuations

    public async Task<IReadOnlyList<HoldingDto>> GetHoldingsAsync(Guid? bucketId, CancellationToken ct)
    {
        var rows = await valuation.ValueHoldingsAsync(RequireUserId(), ct);
        var totalValue = rows.Sum(r => r.Value.ValueMinor);
        var bucketTotals = rows.GroupBy(r => r.BucketId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Value.ValueMinor));

        return rows
            .Where(r => bucketId is null || r.BucketId == bucketId.Value)
            .Select(r => ToHoldingDto(r, bucketTotals.GetValueOrDefault(r.BucketId), totalValue))
            .OrderByDescending(h => h.ValueMinor)
            .ToList();
    }

    public async Task<HoldingDto> GetHoldingAsync(Guid id, CancellationToken ct)
    {
        var rows = await valuation.ValueHoldingsAsync(RequireUserId(), ct);
        var row = rows.FirstOrDefault(r => r.HoldingId == id)
            ?? throw ApiException.NotFound("holding_not_found", "Holding not found.");
        var totalValue = rows.Sum(r => r.Value.ValueMinor);
        var bucketTotal = rows.Where(r => r.BucketId == row.BucketId).Sum(r => r.Value.ValueMinor);

        var lots = await db.Lots.AsNoTracking()
            .Where(l => l.HoldingId == id)
            .OrderBy(l => l.AcquiredAt)
            .Select(l => new LotDto(l.Id, l.Qty, l.CostMinor, l.CostCurrency, l.AcquiredAt, l.Source, l.TradeId))
            .ToListAsync(ct);

        return ToHoldingDto(row, bucketTotal, totalValue) with { Lots = lots };
    }

    private static HoldingDto ToHoldingDto(HoldingValuationRow r, long bucketTotal, long portfolioTotal)
    {
        var value = r.Value;
        var pnl = value.ValueMinor - value.CostBasisMinor;
        return new HoldingDto(
            r.HoldingId,
            r.BucketId,
            new InstrumentDto(r.InstrumentId, r.Symbol, r.InstrumentName, r.AssetClass, null, r.InstrumentCurrency),
            r.ThesisNotesMd,
            r.ManualGrowthRatePct is { } m ? Pct.ToFraction(m) : null,
            value.Qty,
            value.CostBasisMinor,
            value.Qty > 0 ? (long)Math.Round(value.CostBasisMinor / value.Qty) : null,
            value.ValueMinor,
            pnl,
            value.CostBasisMinor != 0 ? pnl / (decimal)value.CostBasisMinor : null,
            bucketTotal > 0 ? value.ValueMinor / (decimal)bucketTotal : 0m,
            portfolioTotal > 0 ? value.ValueMinor / (decimal)portfolioTotal : 0m,
            value.Stale,
            value.Price,
            value.PriceAsOf,
            r.LotCount);
    }

    // ---------------------------------------------------------------- summary

    public async Task<PortfolioSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        var userId = RequireUserId();
        var baseCurrency = await valuation.GetBaseCurrencyAsync(userId, ct);
        var holdingRows = await valuation.ValueHoldingsAsync(userId, ct);
        var bucketEquities = await valuation.ValueBucketsAsync(userId, ct, holdingRows);

        var totalValue = bucketEquities.Sum(b => b.ValueMinor);
        var totalCost = holdingRows.Sum(r => r.Value.CostBasisMinor);

        var perBucket = bucketEquities.Select(b =>
        {
            var target = Pct.ToFraction(b.Bucket.TargetAllocPct);
            var actual = totalValue > 0 ? b.ValueMinor / (decimal)totalValue : 0m;
            // Drift band: 25% relative of the target; flag when |actual - target| > 1.5 x band.
            var band = 0.25m * target;
            var drift = target > 0m && Math.Abs(actual - target) > 1.5m * band;
            return new BucketSummaryDto(
                b.Bucket.Id,
                b.Bucket.Name,
                b.Bucket.Kind,
                b.ValueMinor,
                target,
                actual,
                Pct.ToFraction(b.Bucket.ContributionSplitPct),
                drift,
                b.Bucket.HighWaterMarkMinor,
                b.AnyStale);
        }).ToList();

        var ladder = await GetLadderStateAsync(ct, bucketEquities);
        var todayChange = await ComputeTodayChangeAsync(userId, totalValue, ct);

        return new PortfolioSummaryDto(
            baseCurrency,
            totalValue,
            totalCost,
            holdingRows.Sum(r => r.Value.ValueMinor - r.Value.CostBasisMinor),
            todayChange,
            perBucket,
            ladder);
    }

    private async Task<long?> ComputeTodayChangeAsync(Guid userId, long totalValue, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var previous = await db.Snapshots.AsNoTracking()
            .Where(s => s.BucketId == null && s.Date < today)
            .OrderByDescending(s => s.Date)
            .FirstOrDefaultAsync(ct);
        if (previous is null)
        {
            return null;
        }

        // Exclude external flows since the snapshot so deposits do not show up as gains.
        var flows = await valuation.GetDailyNetFlowsAsync(userId, null, ct);
        var flowsSince = flows.Where(kv => kv.Key > previous.Date).Sum(kv => kv.Value);
        return totalValue - previous.EquityMinor - flowsSince;
    }

    // ----------------------------------------------------------------- ladder

    public async Task<LadderStateDto> GetLadderStateAsync(
        CancellationToken ct, IReadOnlyList<BucketEquity>? bucketEquities = null)
    {
        var userId = RequireUserId();
        bucketEquities ??= await valuation.ValueBucketsAsync(userId, ct);
        var trading = bucketEquities.FirstOrDefault(b => b.Bucket.Kind == BucketKind.Trading);
        var thresholds = await GetLadderThresholdsAsync(ct);

        if (trading is null)
        {
            return new LadderStateDto(LadderState.Normal, 0m, 0, 0, thresholds);
        }

        return BuildLadderState(trading.Bucket.HighWaterMarkMinor, trading.ValueMinor, thresholds);
    }

    public static LadderStateDto BuildLadderState(long hwmMinor, long currentMinor, LadderThresholdsDto thresholds)
    {
        var drawdown = hwmMinor > 0 && currentMinor < hwmMinor
            ? (hwmMinor - currentMinor) / (decimal)hwmMinor
            : 0m;
        var state = drawdown >= thresholds.PaperPct ? LadderState.PaperProposed
            : drawdown >= thresholds.PausedPct ? LadderState.Paused
            : drawdown >= thresholds.RiskHalvedPct ? LadderState.RiskHalved
            : LadderState.Normal;
        return new LadderStateDto(state, drawdown, hwmMinor, currentMinor, thresholds);
    }

    /// <summary>
    /// Reads drawdown thresholds from the active RiskProfile's LadderThresholdsJson when it
    /// carries them ({"riskHalvedPct":0.05,"pausedPct":0.1,"paperPct":0.15}; percent points
    /// tolerated). The shipped R-multiple ladder array does not — defaults 5/10/15% apply.
    /// </summary>
    public async Task<LadderThresholdsDto> GetLadderThresholdsAsync(CancellationToken ct)
    {
        var json = await db.RiskProfiles.AsNoTracking()
            .Where(r => r.IsActive)
            .Select(r => r.LadderThresholdsJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return DefaultLadderThresholds;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return DefaultLadderThresholds;
            }

            var halved = ReadThreshold(doc.RootElement, "riskHalvedPct") ?? DefaultLadderThresholds.RiskHalvedPct;
            var paused = ReadThreshold(doc.RootElement, "pausedPct") ?? DefaultLadderThresholds.PausedPct;
            var paper = ReadThreshold(doc.RootElement, "paperPct") ?? DefaultLadderThresholds.PaperPct;
            return new LadderThresholdsDto(halved, paused, paper);
        }
        catch (JsonException)
        {
            return DefaultLadderThresholds;
        }
    }

    private static decimal? ReadThreshold(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var value = el.GetDecimal();
        return value > 1m ? value / 100m : value; // tolerate percent points
    }

    // ---------------------------------------------------------------- ratchet

    public async Task<RatchetSuggestionDto?> GetRatchetSuggestionAsync(CancellationToken ct)
    {
        var userId = RequireUserId();
        var bucketEquities = await valuation.ValueBucketsAsync(userId, ct);
        var trading = bucketEquities.FirstOrDefault(b => b.Bucket.Kind == BucketKind.Trading);
        if (trading is null)
        {
            return null;
        }

        var profit = trading.ValueMinor - trading.Bucket.HighWaterMarkMinor;
        if (profit <= 0 || trading.Bucket.HighWaterMarkMinor <= 0)
        {
            return null;
        }

        // Quarterly cadence: only suggest when no ratchet happened in the last quarter.
        var quarterAgo = DateTime.UtcNow.AddMonths(-3);
        var recentRatchet = await db.CashFlows.AsNoTracking()
            .AnyAsync(f => f.Type == CashFlowType.Ratchet && f.At >= quarterAgo, ct);
        if (recentRatchet)
        {
            return null;
        }

        return new RatchetSuggestionDto(profit, profit, trading.Bucket.HighWaterMarkMinor, trading.ValueMinor);
    }

    /// <summary>Moves accepted profit Trading → LongTerm as paired Ratchet flows and bumps the HWM.</summary>
    public async Task<AcceptRatchetResultDto> AcceptRatchetAsync(long amountMinor, CancellationToken ct)
    {
        if (amountMinor <= 0)
        {
            throw ApiException.BadRequest("invalid_amount", "amountMinor must be positive.");
        }

        var userId = RequireUserId();
        var trading = await db.Buckets.FirstOrDefaultAsync(b => b.Kind == BucketKind.Trading, ct)
            ?? throw ApiException.NotFound("bucket_not_found", "No Trading bucket exists.");
        var longTerm = await db.Buckets.FirstOrDefaultAsync(b => b.Kind == BucketKind.LongTerm, ct)
            ?? throw ApiException.NotFound("bucket_not_found", "No Long-term bucket exists.");

        var bucketEquities = await valuation.ValueBucketsAsync(userId, ct);
        var currentEquity = bucketEquities.First(b => b.Bucket.Id == trading.Id).ValueMinor;
        if (amountMinor > Math.Max(0, currentEquity))
        {
            throw ApiException.BadRequest(
                "invalid_amount", "amountMinor exceeds the Trading bucket's current equity.");
        }

        var now = DateTime.UtcNow;
        var note = $"Ratchet {amountMinor} Trading → Long-term";
        var outFlow = new CashFlow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BucketId = trading.Id,
            Type = CashFlowType.Ratchet,
            AmountMinor = -amountMinor,
            Currency = trading.Currency,
            At = now,
            Note = note,
        };
        var inFlow = new CashFlow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BucketId = longTerm.Id,
            Type = CashFlowType.Ratchet,
            AmountMinor = amountMinor,
            Currency = longTerm.Currency,
            At = now,
            Note = note,
        };
        db.CashFlows.AddRange(outFlow, inFlow);

        // The HWM ratchets up to the post-transfer equity (never down).
        var newHwm = Math.Max(trading.HighWaterMarkMinor, currentEquity - amountMinor);
        trading.HighWaterMarkMinor = newHwm;
        await db.SaveChangesAsync(ct);

        var thresholds = await GetLadderThresholdsAsync(ct);
        return new AcceptRatchetResultDto(
            outFlow.Id,
            inFlow.Id,
            newHwm,
            BuildLadderState(newHwm, currentEquity - amountMinor, thresholds));
    }

    // ------------------------------------------------------------ performance

    public async Task<PerformanceDto> GetPerformanceAsync(
        Guid? bucketId, string basis, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var userId = RequireUserId();
        basis = string.IsNullOrWhiteSpace(basis) ? "twr" : basis.ToLowerInvariant();
        if (basis is not ("twr" or "mwr"))
        {
            throw ApiException.BadRequest("invalid_basis", "basis must be 'twr' or 'mwr'.");
        }

        if (basis == "mwr")
        {
            return await ComputeMwrAsync(userId, bucketId, from, to, ct);
        }

        var snapshots = await db.Snapshots.AsNoTracking()
            .Where(s => s.BucketId == bucketId)
            .Where(s => (from == null || s.Date >= from) && (to == null || s.Date <= to))
            .OrderBy(s => s.Date)
            .Select(s => new EquitySnapshot(s.Date, s.EquityMinor))
            .ToListAsync(ct);

        var flowsByDate = await valuation.GetDailyNetFlowsAsync(userId, bucketId, ct);
        var flows = flowsByDate
            .Where(kv => (from == null || kv.Key >= from) && (to == null || kv.Key <= to))
            .Select(kv => new NetFlow(kv.Key, kv.Value))
            .ToList();

        var twr = PerformanceEngine.ComputeTwr(snapshots, flows);
        var points = twr.Points
            .Select(p => new PerformancePointDto(p.Date, p.PeriodReturn, p.CumulativeReturn))
            .ToList();
        var days = snapshots.Count >= 2 ? snapshots[^1].Date.DayNumber - snapshots[0].Date.DayNumber : 0;

        return new PerformanceDto(
            "twr",
            points,
            twr.TotalReturn,
            PerformanceEngine.AnnualizedReturn(twr.TotalReturn, days),
            Xirr: null,
            PerformanceEngine.MaxDrawdown(snapshots));
    }

    private async Task<PerformanceDto> ComputeMwrAsync(
        Guid userId, Guid? bucketId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var flowsByDate = await valuation.GetDailyNetFlowsAsync(userId, bucketId, ct);
        // Investor perspective: deposits are investments (negative), withdrawals positive.
        var items = flowsByDate
            .Where(kv => (from == null || kv.Key >= from) && (to == null || kv.Key <= to))
            .Select(kv => new CashflowItem(kv.Key, -kv.Value))
            .ToList();

        long terminal;
        if (bucketId is null)
        {
            terminal = (await valuation.ValueBucketsAsync(userId, ct)).Sum(b => b.ValueMinor);
        }
        else
        {
            var equities = await valuation.ValueBucketsAsync(userId, ct);
            terminal = equities.FirstOrDefault(b => b.Bucket.Id == bucketId.Value)?.ValueMinor
                ?? throw ApiException.NotFound("bucket_not_found", "Bucket not found.");
        }

        items.Add(new CashflowItem(DateOnly.FromDateTime(DateTime.UtcNow), terminal));
        var xirr = PerformanceEngine.ComputeXirr(items);
        return new PerformanceDto("mwr", [], TotalReturn: null, AnnualizedReturn: null, xirr, MaxDrawdown: null);
    }

    // ------------------------------------------------------------- allocation

    public async Task<AllocationDto> GetAllocationAsync(CancellationToken ct)
    {
        var userId = RequireUserId();
        var baseCurrency = await valuation.GetBaseCurrencyAsync(userId, ct);
        var holdingRows = await valuation.ValueHoldingsAsync(userId, ct);
        var bucketEquities = await valuation.ValueBucketsAsync(userId, ct, holdingRows);
        var total = bucketEquities.Sum(b => b.ValueMinor);

        var perBucket = bucketEquities
            .Select(b => new AllocationSliceDto(
                b.Bucket.Id.ToString(),
                b.Bucket.Name,
                b.ValueMinor,
                total > 0 ? b.ValueMinor / (decimal)total : 0m))
            .ToList();

        // Cash-kind bucket balances (flows without holdings) count as Cash asset class.
        var cashAdjust = bucketEquities
            .Where(b => b.Bucket.Kind == BucketKind.Cash)
            .Sum(b => b.ValueMinor)
            - holdingRows.Where(r => bucketEquities.Any(b =>
                    b.Bucket.Id == r.BucketId && b.Bucket.Kind == BucketKind.Cash))
                .Sum(r => r.Value.ValueMinor);

        var perAssetClass = holdingRows
            .GroupBy(r => r.AssetClass)
            .Select(g => new { Key = g.Key, Value = g.Sum(r => r.Value.ValueMinor) })
            .ToList();
        if (cashAdjust != 0)
        {
            var existing = perAssetClass.FirstOrDefault(a => a.Key == AssetClass.Cash);
            if (existing is not null)
            {
                perAssetClass.Remove(existing);
                perAssetClass.Add(new { Key = AssetClass.Cash, Value = existing.Value + cashAdjust });
            }
            else
            {
                perAssetClass.Add(new { Key = AssetClass.Cash, Value = cashAdjust });
            }
        }

        var assetSlices = perAssetClass
            .OrderByDescending(a => a.Value)
            .Select(a => new AllocationSliceDto(
                JsonNamingPolicy.CamelCase.ConvertName(a.Key.ToString()),
                a.Key.ToString(),
                a.Value,
                total > 0 ? a.Value / (decimal)total : 0m))
            .ToList();

        return new AllocationDto(baseCurrency, total, perBucket, assetSlices);
    }

    // -------------------------------------------------------------- net worth

    public async Task<IReadOnlyList<NetworthPointDto>> GetNetworthAsync(
        DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        RequireUserId();
        return await db.Snapshots.AsNoTracking()
            .Where(s => s.BucketId == null)
            .Where(s => (from == null || s.Date >= from) && (to == null || s.Date <= to))
            .OrderBy(s => s.Date)
            .Select(s => new NetworthPointDto(s.Date, s.EquityMinor, s.NetFlowMinor))
            .ToListAsync(ct);
    }
}

/// <summary>Registers the user-scoped portfolio services.</summary>
public sealed class PortfolioModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<PortfolioService>();
        services.AddScoped<ForecastRunner>();
    }
}
