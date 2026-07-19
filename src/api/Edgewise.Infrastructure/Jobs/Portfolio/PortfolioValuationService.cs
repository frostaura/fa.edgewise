using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Infrastructure.Jobs.Portfolio;

/// <summary>One valued holding (base currency, minor units).</summary>
public sealed record HoldingValuationRow(
    Guid HoldingId,
    Guid BucketId,
    Guid InstrumentId,
    string Symbol,
    string InstrumentName,
    AssetClass AssetClass,
    string InstrumentCurrency,
    string? ThesisNotesMd,
    decimal? ManualGrowthRatePct,
    int LotCount,
    DateTime? OldestLotAt,
    HoldingValueResult Value);

/// <summary>One valued bucket (base currency, minor units).</summary>
public sealed record BucketEquity(Bucket Bucket, long ValueMinor, bool AnyStale);

/// <summary>
/// Values a user's holdings and buckets in the user's base currency, minor units.
/// Takes an explicit user id and bypasses the per-user query filters so it can serve
/// both authenticated API requests and background jobs (Hangfire runs with no user).
/// Consumed by the portfolio endpoints, the snapshot job, and the cockpit vertical
/// (equity / heat inputs).
/// </summary>
public sealed class PortfolioValuationService(EdgewiseDbContext db, PortfolioMarketData marketData)
{
    /// <summary>Cash flow types that are external capital movements (feed TWR net flows / snapshots).</summary>
    public static readonly CashFlowType[] ExternalFlowTypes =
        [CashFlowType.Deposit, CashFlowType.Withdrawal, CashFlowType.Transfer, CashFlowType.Ratchet];

    public async Task<string> GetBaseCurrencyAsync(Guid userId, CancellationToken ct)
    {
        var currency = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => u.BaseCurrency)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(currency) ? "ZAR" : currency;
    }

    /// <summary>Values every holding of the user (best-effort quotes; cost-basis fallback flagged stale).</summary>
    public async Task<IReadOnlyList<HoldingValuationRow>> ValueHoldingsAsync(Guid userId, CancellationToken ct)
    {
        var baseCurrency = await GetBaseCurrencyAsync(userId, ct);
        var nowUtc = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(nowUtc);

        var holdings = await db.Holdings.IgnoreQueryFilters().AsNoTracking()
            .Where(h => h.UserId == userId)
            .Join(
                db.Instruments.AsNoTracking(),
                h => h.InstrumentId,
                i => i.Id,
                (h, i) => new { Holding = h, Instrument = i })
            .ToListAsync(ct);

        if (holdings.Count == 0)
        {
            return [];
        }

        var holdingIds = holdings.Select(x => x.Holding.Id).ToList();
        var lotsByHolding = (await db.Lots.IgnoreQueryFilters().AsNoTracking()
                .Where(l => holdingIds.Contains(l.HoldingId))
                .ToListAsync(ct))
            .GroupBy(l => l.HoldingId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Prefetch FX for every currency in play.
        var currencies = holdings.Select(x => x.Instrument.Currency)
            .Concat(lotsByHolding.Values.SelectMany(l => l).Select(l => l.CostCurrency))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fxMap = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        foreach (var currency in currencies)
        {
            fxMap[currency] = await marketData.GetFxRateAsync(currency, baseCurrency, today, ct);
        }

        decimal? FxToBase(string currency) =>
            string.IsNullOrWhiteSpace(currency) ? 1m : fxMap.GetValueOrDefault(currency);

        var rows = new List<HoldingValuationRow>(holdings.Count);
        foreach (var entry in holdings)
        {
            var holding = entry.Holding;
            var instrument = entry.Instrument;
            var lots = lotsByHolding.GetValueOrDefault(holding.Id) ?? [];
            var isManual = instrument.AssetClass is AssetClass.Cash or AssetClass.Custom;

            QuoteInput? quote = null;
            if (!isManual && !holding.ManualGrowthRatePct.HasValue && lots.Count > 0)
            {
                quote = await marketData.GetQuoteAsync(instrument.Id, instrument.Currency, ct);
            }

            var value = HoldingValuation.Compute(
                lots.Select(l => new LotInput(l.Qty, l.CostMinor, l.CostCurrency, l.AcquiredAt)).ToList(),
                isManual,
                holding.ManualGrowthRatePct,
                quote,
                FxToBase,
                nowUtc);

            rows.Add(new HoldingValuationRow(
                holding.Id,
                holding.BucketId,
                instrument.Id,
                instrument.Symbol,
                instrument.Name,
                instrument.AssetClass,
                instrument.Currency,
                holding.ThesisNotesMd,
                holding.ManualGrowthRatePct,
                lots.Count,
                lots.Count > 0 ? lots.Min(l => l.AcquiredAt) : null,
                value));
        }

        return rows;
    }

    /// <summary>
    /// Bucket equities: sum of holding values, plus (for Cash-kind buckets) the net of
    /// all signed cash flows — cash buckets typically hold flows rather than lots.
    /// </summary>
    public async Task<IReadOnlyList<BucketEquity>> ValueBucketsAsync(
        Guid userId, CancellationToken ct, IReadOnlyList<HoldingValuationRow>? holdingRows = null)
    {
        var buckets = await db.Buckets.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.UserId == userId)
            .OrderBy(b => b.Kind)
            .ToListAsync(ct);

        holdingRows ??= await ValueHoldingsAsync(userId, ct);
        var byBucket = holdingRows.GroupBy(r => r.BucketId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var cashBucketIds = buckets.Where(b => b.Kind == BucketKind.Cash).Select(b => b.Id).ToList();
        var cashBalances = cashBucketIds.Count == 0
            ? []
            : await db.CashFlows.IgnoreQueryFilters().AsNoTracking()
                .Where(f => f.UserId == userId && cashBucketIds.Contains(f.BucketId))
                .GroupBy(f => f.BucketId)
                .Select(g => new { BucketId = g.Key, Sum = g.Sum(f => f.AmountMinor) })
                .ToDictionaryAsync(x => x.BucketId, x => x.Sum, ct);

        var result = new List<BucketEquity>(buckets.Count);
        foreach (var bucket in buckets)
        {
            var rows = byBucket.GetValueOrDefault(bucket.Id) ?? [];
            var value = rows.Sum(r => r.Value.ValueMinor);
            if (bucket.Kind == BucketKind.Cash)
            {
                value += cashBalances.GetValueOrDefault(bucket.Id);
            }

            result.Add(new BucketEquity(bucket, value, rows.Any(r => r.Value.Stale)));
        }

        return result;
    }

    /// <summary>Net external flow (signed, minor units) per day for TWR / snapshot annotations.</summary>
    public async Task<Dictionary<DateOnly, long>> GetDailyNetFlowsAsync(
        Guid userId, Guid? bucketId, CancellationToken ct)
    {
        var query = db.CashFlows.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.UserId == userId && ExternalFlowTypes.Contains(f.Type));
        if (bucketId.HasValue)
        {
            query = query.Where(f => f.BucketId == bucketId.Value);
        }

        var flows = await query.Select(f => new { f.At, f.AmountMinor }).ToListAsync(ct);
        return flows
            .GroupBy(f => DateOnly.FromDateTime(f.At))
            .ToDictionary(g => g.Key, g => g.Sum(f => f.AmountMinor));
    }
}
