using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.MarketData;

/// <summary>Latest-quote answer. When every provider fails the last cached price is returned with <see cref="Stale"/> true.</summary>
public sealed record QuoteResult(decimal Price, DateTime AsOf, string Provider, bool Stale, int AgeMinutes);

/// <summary>Bars from storage; <see cref="Stale"/> is true when the closed-bar tail could not be refreshed.</summary>
public sealed record BarsResult(IReadOnlyList<PriceBar> Bars, bool Stale);

/// <summary>A resolved FX rate (possibly from a nearby earlier date, weekend walk-back).</summary>
public sealed record FxRateResult(decimal Rate, DateOnly Date, string Provider);

/// <summary>
/// The market-data facade the rest of the app consumes. Serves closed bars and
/// quotes from PostgreSQL, fetching only the missing tail through per-asset-class
/// provider fallback chains, and records ProviderHealth on every provider touch.
/// </summary>
public sealed class MarketDataService(
    EdgewiseDbContext db,
    IEnumerable<IBarProvider> barProviders,
    IEnumerable<IQuoteProvider> quoteProviders,
    IEnumerable<IFxProvider> fxProviders,
    ProviderHealthWriter health,
    ILogger<MarketDataService> logger)
{
    /// <summary>A cached quote younger than this is served without touching any provider.</summary>
    public static readonly TimeSpan QuoteFreshness = TimeSpan.FromMinutes(15);

    /// <summary>Maximum number of days walked backwards when resolving an FX rate.</summary>
    public const int FxWalkBackDays = 7;

    private static readonly string[] EquityBarChain = [ProviderNames.Yahoo, ProviderNames.Stooq];
    private static readonly string[] CryptoBarChain = [ProviderNames.Binance, ProviderNames.Coinbase];
    private static readonly string[] EquityQuoteChain = [ProviderNames.Yahoo, ProviderNames.Stooq];
    private static readonly string[] CryptoQuoteChain =
        [ProviderNames.Binance, ProviderNames.Coinbase, ProviderNames.CoinGecko];
    private static readonly string[] FxChain = [ProviderNames.Frankfurter, ProviderNames.ExchangeRateApi];

    private readonly Dictionary<string, IBarProvider> _barProviders = ByName(barProviders);
    private readonly Dictionary<string, IQuoteProvider> _quoteProviders = ByName(quoteProviders);
    private readonly Dictionary<string, IFxProvider> _fxProviders = ByName(fxProviders);

    // ------------------------------------------------------------------ bars

    /// <summary>
    /// Returns closed bars for [fromUtc, toUtc] from the PriceBar table, fetching only
    /// the missing tail from the instrument's provider chain (fetched closed bars are
    /// persisted with insert-ignore, so nothing is ever refetched). An optional
    /// <paramref name="fetchTimeout"/> bounds the inline provider fetch; when it
    /// expires the stored bars are returned with Stale=true.
    /// </summary>
    public async Task<BarsResult> GetBarsAsync(
        Guid instrumentId,
        Timeframe timeframe,
        DateTime fromUtc,
        DateTime toUtc,
        TimeSpan? fetchTimeout = null,
        CancellationToken ct = default)
    {
        var instrument = await db.Instruments.FindAsync([instrumentId], ct)
            ?? throw ApiException.NotFound("instrument_not_found", "Instrument does not exist.");

        fromUtc = AsUtc(fromUtc);
        toUtc = AsUtc(toUtc);
        var now = DateTime.UtcNow;
        if (toUtc > now)
        {
            toUtc = now;
        }

        var targetEnd = Min(
            TimeframeMath.FloorToBarOpen(toUtc, timeframe),
            TimeframeMath.LastClosedBarOpen(now, timeframe));

        var maxStored = await db.PriceBars
            .Where(b => b.InstrumentId == instrumentId && b.Timeframe == timeframe)
            .MaxAsync(b => (DateTime?)b.Ts, ct);

        var chain = BarChainFor(instrument.AssetClass);
        var stale = false;
        if (chain.Length > 0 && (maxStored is null || maxStored < targetEnd))
        {
            var fetchFrom = maxStored is { } stored
                ? stored + TimeframeMath.Duration(timeframe)
                : TimeframeMath.FloorToBarOpen(fromUtc, timeframe);
            var fetchedTo = await FetchAndStoreBarsAsync(
                instrument, timeframe, fetchFrom, toUtc, now, chain, fetchTimeout, ct);
            var effectiveMax = fetchedTo ?? maxStored;
            stale = effectiveMax is null || effectiveMax < targetEnd;
        }

        var rangeStart = TimeframeMath.FloorToBarOpen(fromUtc, timeframe);
        var bars = await db.PriceBars.AsNoTracking()
            .Where(b => b.InstrumentId == instrumentId && b.Timeframe == timeframe
                && b.Ts >= rangeStart && b.Ts <= toUtc)
            .OrderBy(b => b.Ts)
            .ToListAsync(ct);
        return new BarsResult(bars, stale);
    }

    /// <summary>Runs the provider chain and persists closed bars. Returns the newest stored bar-open, or null when nothing was stored.</summary>
    private async Task<DateTime?> FetchAndStoreBarsAsync(
        Instrument instrument,
        Timeframe timeframe,
        DateTime fetchFromUtc,
        DateTime toUtc,
        DateTime nowUtc,
        string[] chain,
        TimeSpan? fetchTimeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (fetchTimeout is { } timeout)
        {
            cts.CancelAfter(timeout);
        }

        foreach (var providerName in chain)
        {
            if (!_barProviders.TryGetValue(providerName, out var provider)
                || ProviderSymbols.Resolve(instrument, providerName) is not { } symbol)
            {
                continue;
            }

            try
            {
                var fetched = await provider.GetBarsAsync(symbol, timeframe, fetchFromUtc, toUtc, cts.Token);
                var closed = fetched
                    .Where(bar => bar.Ts >= fetchFromUtc && TimeframeMath.IsClosed(bar.Ts, timeframe, nowUtc))
                    .OrderBy(bar => bar.Ts)
                    .ToList();
                if (closed.Count == 0)
                {
                    continue; // Provider answered but cannot serve this symbol/timeframe; try the next one.
                }

                await InsertBarsAsync(instrument.Id, timeframe, closed, providerName, ct);
                await health.ReportSuccessAsync(providerName, ct);
                return closed[^1].Ts;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Bar fetch budget exhausted for {Symbol} {Timeframe} on {Provider}.",
                    instrument.Symbol, timeframe, providerName);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "Bar fetch failed for {Symbol} {Timeframe} on {Provider}.",
                    instrument.Symbol, timeframe, providerName);
                await health.ReportErrorAsync(providerName, ex.Message, ct);
            }
        }

        return null;
    }

    private async Task InsertBarsAsync(
        Guid instrumentId, Timeframe timeframe, IReadOnlyList<ProviderBar> bars, string provider, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var bar in bars)
        {
            var ts = AsUtc(bar.Ts);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "PriceBars" ("InstrumentId", "Timeframe", "Ts", "O", "H", "L", "C", "V", "Provider")
                VALUES ({instrumentId}, {(int)timeframe}, {ts}, {bar.O}, {bar.H}, {bar.L}, {bar.C}, {bar.V}, {provider})
                ON CONFLICT DO NOTHING
                """,
                ct);
        }

        await transaction.CommitAsync(ct);
    }

    // ---------------------------------------------------------------- quotes

    /// <summary>
    /// Fresh cache hit (≤15 min) → cached price; otherwise the chain is walked and the
    /// cache updated. When every provider fails the last cached price is returned with
    /// Stale=true; null means no price was ever obtainable.
    /// </summary>
    public async Task<QuoteResult?> GetQuoteAsync(Guid instrumentId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var cached = await db.QuoteCaches.AsNoTracking()
            .FirstOrDefaultAsync(q => q.InstrumentId == instrumentId, ct);
        if (cached is not null && now - cached.AsOf <= QuoteFreshness)
        {
            return new QuoteResult(cached.Price, cached.AsOf, cached.Provider, Stale: false, AgeMinutes(now, cached.AsOf));
        }

        var instrument = await db.Instruments.FindAsync([instrumentId], ct)
            ?? throw ApiException.NotFound("instrument_not_found", "Instrument does not exist.");

        foreach (var providerName in QuoteChainFor(instrument.AssetClass))
        {
            if (!_quoteProviders.TryGetValue(providerName, out var provider)
                || ProviderSymbols.Resolve(instrument, providerName) is not { } symbol)
            {
                continue;
            }

            try
            {
                var price = await provider.GetQuoteAsync(symbol, ct);
                await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO "QuoteCaches" ("InstrumentId", "Price", "AsOf", "Provider")
                    VALUES ({instrumentId}, {price}, {now}, {providerName})
                    ON CONFLICT ("InstrumentId") DO UPDATE
                    SET "Price" = EXCLUDED."Price", "AsOf" = EXCLUDED."AsOf", "Provider" = EXCLUDED."Provider"
                    """,
                    ct);
                await health.ReportSuccessAsync(providerName, ct);
                return new QuoteResult(price, now, providerName, Stale: false, AgeMinutes: 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Quote fetch failed for {Symbol} on {Provider}.", instrument.Symbol, providerName);
                await health.ReportErrorAsync(providerName, ex.Message, ct);
            }
        }

        return cached is null
            ? null
            : new QuoteResult(cached.Price, cached.AsOf, cached.Provider, Stale: true, AgeMinutes(now, cached.AsOf));
    }

    // ------------------------------------------------------------------- fx

    /// <summary>
    /// Daily FX rate with a ≤7-day walk-back (weekends/holidays). Serves from the
    /// FxRate table (direct or inverted) before hitting the chain; fetched rates
    /// are persisted. Null when no provider can supply the pair.
    /// </summary>
    public async Task<FxRateResult?> GetFxRateAsync(
        string baseCurrency, string quoteCurrency, DateOnly date, CancellationToken ct = default)
    {
        baseCurrency = baseCurrency.Trim().ToUpperInvariant();
        quoteCurrency = quoteCurrency.Trim().ToUpperInvariant();
        if (baseCurrency == quoteCurrency)
        {
            return new FxRateResult(1m, date, "identity");
        }

        for (var back = 0; back < FxWalkBackDays; back++)
        {
            var day = date.AddDays(-back);
            var stored = await db.FxRates.AsNoTracking().FirstOrDefaultAsync(
                f => (f.Base == baseCurrency && f.Quote == quoteCurrency && f.Date == day)
                    || (f.Base == quoteCurrency && f.Quote == baseCurrency && f.Date == day),
                ct);
            if (stored is not null && stored.Rate != 0m)
            {
                var rate = stored.Base == baseCurrency ? stored.Rate : 1m / stored.Rate;
                return new FxRateResult(rate, day, stored.Provider);
            }
        }

        foreach (var providerName in FxChain)
        {
            if (!_fxProviders.TryGetValue(providerName, out var provider))
            {
                continue;
            }

            try
            {
                for (var back = 0; back < FxWalkBackDays; back++)
                {
                    var day = date.AddDays(-back);
                    var rate = await provider.GetDailyRateAsync(baseCurrency, quoteCurrency, day, ct);
                    if (rate is not { } value || value <= 0m)
                    {
                        continue;
                    }

                    await db.Database.ExecuteSqlAsync(
                        $"""
                        INSERT INTO "FxRates" ("Base", "Quote", "Date", "Rate", "Provider")
                        VALUES ({baseCurrency}, {quoteCurrency}, {day}, {value}, {providerName})
                        ON CONFLICT ("Base", "Quote", "Date") DO UPDATE
                        SET "Rate" = EXCLUDED."Rate", "Provider" = EXCLUDED."Provider"
                        """,
                        ct);
                    await health.ReportSuccessAsync(providerName, ct);
                    return new FxRateResult(value, day, providerName);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "FX fetch failed for {Base}/{Quote} on {Provider}.", baseCurrency, quoteCurrency, providerName);
                await health.ReportErrorAsync(providerName, ex.Message, ct);
            }
        }

        return null;
    }

    // -------------------------------------------------------------- helpers

    /// <summary>
    /// Valuation helper for manual-growth instruments (AssetClass Cash/Custom):
    /// compounds the holding's declared annual growth rate over elapsed time
    /// instead of consulting any market provider.
    /// </summary>
    public static decimal ProjectManualGrowthValue(
        decimal baseValue, decimal annualGrowthRatePct, DateTime fromUtc, DateTime asOfUtc)
    {
        if (asOfUtc <= fromUtc || annualGrowthRatePct == 0m)
        {
            return baseValue;
        }

        var years = (asOfUtc - fromUtc).TotalDays / 365.25;
        var factor = Math.Pow(1d + ((double)annualGrowthRatePct / 100d), years);
        return baseValue * (decimal)factor;
    }

    /// <summary>Instrument ids referenced by any Holding or WatchlistItem across all users (query filters bypassed).</summary>
    public async Task<List<Guid>> GetTrackedInstrumentIdsAsync(CancellationToken ct = default)
    {
        var held = db.Holdings.IgnoreQueryFilters().Select(h => h.InstrumentId);
        var watched = db.WatchlistItems.IgnoreQueryFilters().Select(w => w.InstrumentId);
        return await held.Union(watched).ToListAsync(ct);
    }

    private static string[] BarChainFor(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Equity or AssetClass.Etf => EquityBarChain,
        AssetClass.Crypto => CryptoBarChain,
        _ => [],
    };

    private static string[] QuoteChainFor(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Equity or AssetClass.Etf => EquityQuoteChain,
        AssetClass.Crypto => CryptoQuoteChain,
        _ => [],
    };

    private static Dictionary<string, T> ByName<T>(IEnumerable<T> providers)
        where T : IMarketDataProvider
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            map[provider.Name] = provider; // last registration wins (lets tests overlay fakes)
        }

        return map;
    }

    private static int AgeMinutes(DateTime nowUtc, DateTime asOfUtc) =>
        (int)Math.Max(0, (nowUtc - asOfUtc).TotalMinutes);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
