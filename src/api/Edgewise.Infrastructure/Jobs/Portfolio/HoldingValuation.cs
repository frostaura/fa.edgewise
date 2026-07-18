namespace Edgewise.Infrastructure.Jobs.Portfolio;

/// <summary>A lot as needed for valuation.</summary>
public sealed record LotInput(decimal Qty, long CostMinor, string CostCurrency, DateTime AcquiredAt);

/// <summary>A resolved quote in the instrument's own currency (major units).</summary>
public sealed record QuoteInput(decimal Price, DateTime AsOf, string Currency, bool Stale);

/// <summary>Valuation result in the user's base currency, minor units.</summary>
public sealed record HoldingValueResult(
    decimal Qty,
    long CostBasisMinor,
    long ValueMinor,
    bool Stale,
    decimal? Price,
    DateTime? PriceAsOf);

/// <summary>
/// Pure holding valuation shared by the Api portfolio service and the snapshot job.
/// Values are minor units in the user's base currency; <c>fxToBase</c> converts a
/// 3-letter currency into the base currency (1.0 for the base itself, null when
/// unknown — the raw amount is then used unconverted as a best-effort fallback).
/// </summary>
public static class HoldingValuation
{
    /// <summary>Minor units per major unit (cents) — uniform across supported currencies.</summary>
    public const decimal MinorPerMajor = 100m;

    /// <summary>Quotes older than this are flagged stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <param name="lots">The holding's lots.</param>
    /// <param name="isManualAsset">True for Cash/Custom instruments valued by declared growth instead of quotes.</param>
    /// <param name="manualGrowthRatePct">Declared APR in percent points (5 = 5%/year); null means 0 for manual assets.</param>
    /// <param name="quote">Best-effort quote; null falls back to cost basis (stale).</param>
    /// <param name="fxToBase">Currency → base-currency conversion rate resolver.</param>
    /// <param name="nowUtc">Valuation instant.</param>
    public static HoldingValueResult Compute(
        IReadOnlyList<LotInput> lots,
        bool isManualAsset,
        decimal? manualGrowthRatePct,
        QuoteInput? quote,
        Func<string, decimal?> fxToBase,
        DateTime nowUtc)
    {
        var qty = lots.Sum(l => l.Qty);
        var costBasis = lots.Sum(l => ConvertMinor(l.CostMinor, l.CostCurrency, fxToBase));

        if (lots.Count == 0)
        {
            return new HoldingValueResult(0m, 0, 0, Stale: false, Price: null, PriceAsOf: null);
        }

        if (isManualAsset || manualGrowthRatePct.HasValue)
        {
            // Manual asset: each lot compounds at the declared APR from acquisition.
            var rate = manualGrowthRatePct ?? 0m;
            long value = 0;
            foreach (var lot in lots)
            {
                var years = Math.Max(0.0, (nowUtc - lot.AcquiredAt).TotalDays / 365.25);
                var grown = (decimal)((double)lot.CostMinor * Math.Pow(1.0 + ((double)rate / 100.0), years));
                value += ConvertMinor((long)Math.Round(grown), lot.CostCurrency, fxToBase);
            }

            return new HoldingValueResult(qty, costBasis, value, Stale: false, Price: null, PriceAsOf: null);
        }

        if (quote is null)
        {
            // No quote available at all: last-known cost basis, flagged stale.
            return new HoldingValueResult(qty, costBasis, costBasis, Stale: true, Price: null, PriceAsOf: null);
        }

        var fx = fxToBase(quote.Currency);
        if (fx is null)
        {
            return new HoldingValueResult(qty, costBasis, costBasis, Stale: true, quote.Price, quote.AsOf);
        }

        var valueMinor = (long)Math.Round(qty * quote.Price * fx.Value * MinorPerMajor);
        var stale = quote.Stale || nowUtc - quote.AsOf > StaleAfter;
        return new HoldingValueResult(qty, costBasis, valueMinor, stale, quote.Price, quote.AsOf);
    }

    private static long ConvertMinor(long amountMinor, string currency, Func<string, decimal?> fxToBase)
    {
        var fx = fxToBase(currency);
        return fx is null or 1m ? amountMinor : (long)Math.Round(amountMinor * fx.Value);
    }
}
