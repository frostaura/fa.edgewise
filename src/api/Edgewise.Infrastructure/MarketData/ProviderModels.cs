using Edgewise.Domain.Entities;

namespace Edgewise.Infrastructure.MarketData;

/// <summary>A single OHLCV bar as returned by an external provider. <see cref="Ts"/> is the UTC bar-open time.</summary>
public readonly record struct ProviderBar(DateTime Ts, decimal O, decimal H, decimal L, decimal C, decimal V);

/// <summary>A news article fetched from a feed or news API.</summary>
public sealed record FetchedNewsItem(string Title, string Url, string Source, DateTime PublishedAt, string? Summary);

/// <summary>An upcoming catalyst (earnings print, macro release) from a calendar provider.</summary>
public sealed record FetchedCalendarEvent(
    CatalystKind Kind,
    string? Symbol,
    string Title,
    DateTime At,
    CatalystSeverity Severity,
    string SourceRef);

/// <summary>A single fear/greed style sentiment observation.</summary>
public sealed record SentimentPoint(DateOnly Date, int Value, string Label);

/// <summary>Well-known provider names used as chain keys and ProviderSymbolsJson keys.</summary>
public static class ProviderNames
{
    public const string Yahoo = "yahoo";
    public const string Stooq = "stooq";
    public const string Binance = "binance";
    public const string Coinbase = "coinbase";
    public const string CoinGecko = "coingecko";
    public const string Frankfurter = "frankfurter";
    public const string ExchangeRateApi = "exchangerate-api";
    public const string AlternativeMe = "alternative-me";
    public const string Finnhub = "finnhub";
    public const string Rss = "rss";
}

/// <summary>Base marker for all external market-data adapters.</summary>
public interface IMarketDataProvider
{
    /// <summary>Stable provider key (also used for ProviderHealth rows and ProviderSymbolsJson lookups).</summary>
    string Name { get; }
}

/// <summary>Historical OHLCV bars capability.</summary>
public interface IBarProvider : IMarketDataProvider
{
    /// <summary>
    /// Fetches bars for <paramref name="providerSymbol"/> covering [fromUtc, toUtc].
    /// Returns an empty list when the provider cannot serve the timeframe/symbol;
    /// throws on transport or upstream errors.
    /// </summary>
    Task<IReadOnlyList<ProviderBar>> GetBarsAsync(
        string providerSymbol, Timeframe timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

/// <summary>Latest-price capability.</summary>
public interface IQuoteProvider : IMarketDataProvider
{
    /// <summary>Fetches the latest traded/indicative price. Throws on failure.</summary>
    Task<decimal> GetQuoteAsync(string providerSymbol, CancellationToken ct);
}

/// <summary>Daily FX rate capability.</summary>
public interface IFxProvider : IMarketDataProvider
{
    /// <summary>Returns the rate for one unit of <paramref name="baseCurrency"/> in <paramref name="quoteCurrency"/>, or null when the provider has no rate for that date.</summary>
    Task<decimal?> GetDailyRateAsync(string baseCurrency, string quoteCurrency, DateOnly date, CancellationToken ct);
}

/// <summary>Fear/greed style sentiment capability.</summary>
public interface ISentimentProvider : IMarketDataProvider
{
    Task<IReadOnlyList<SentimentPoint>> GetRecentAsync(int days, CancellationToken ct);
}

/// <summary>Bulk headline feed capability (RSS et al.).</summary>
public interface INewsFeedProvider : IMarketDataProvider
{
    Task<IReadOnlyList<FetchedNewsItem>> GetLatestAsync(CancellationToken ct);
}

/// <summary>Per-symbol news capability (Finnhub company-news).</summary>
public interface ICompanyNewsProvider : IMarketDataProvider
{
    Task<IReadOnlyList<FetchedNewsItem>> GetCompanyNewsAsync(
        string providerSymbol, DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>Earnings + macro calendar capability.</summary>
public interface ICalendarProvider : IMarketDataProvider
{
    Task<IReadOnlyList<FetchedCalendarEvent>> GetEarningsAsync(DateOnly from, DateOnly to, CancellationToken ct);

    Task<IReadOnlyList<FetchedCalendarEvent>> GetEconomicEventsAsync(DateOnly from, DateOnly to, CancellationToken ct);
}
