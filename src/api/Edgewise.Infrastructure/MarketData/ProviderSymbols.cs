using System.Text.Json;
using Edgewise.Domain.Entities;

namespace Edgewise.Infrastructure.MarketData;

/// <summary>
/// Resolves the symbol an external provider expects for an <see cref="Instrument"/>.
/// Explicit entries in <see cref="Instrument.ProviderSymbolsJson"/> (keys "yahoo",
/// "stooq", "binance", "coinbase", "coingecko", "finnhub") always win; otherwise a
/// best-effort default is derived from the canonical symbol.
/// </summary>
public static class ProviderSymbols
{
    private static readonly HashSet<string> UsExchanges =
        new(StringComparer.OrdinalIgnoreCase) { "NASDAQ", "NYSE", "AMEX", "ARCA", "BATS", "US" };

    public static Dictionary<string, string>? Parse(string? providerSymbolsJson)
    {
        if (string.IsNullOrWhiteSpace(providerSymbolsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(providerSymbolsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Returns the provider-specific symbol, or null when the instrument cannot be mapped to the provider.</summary>
    public static string? Resolve(Instrument instrument, string provider)
    {
        var explicitMap = Parse(instrument.ProviderSymbolsJson);
        if (explicitMap is not null && explicitMap.TryGetValue(provider, out var mapped)
            && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        var symbol = instrument.Symbol.Trim();
        if (symbol.Length == 0)
        {
            return null;
        }

        return provider switch
        {
            ProviderNames.Yahoo => symbol,
            ProviderNames.Finnhub => symbol,
            ProviderNames.Stooq => DefaultStooq(symbol, instrument.Exchange),
            ProviderNames.Binance when instrument.AssetClass == AssetClass.Crypto =>
                symbol.Contains("USDT", StringComparison.OrdinalIgnoreCase)
                    ? symbol.ToUpperInvariant()
                    : symbol.ToUpperInvariant() + "USDT",
            ProviderNames.Coinbase when instrument.AssetClass == AssetClass.Crypto =>
                symbol.Contains('-') ? symbol.ToUpperInvariant() : symbol.ToUpperInvariant() + "-USD",
            // CoinGecko ids (e.g. "bitcoin") are not derivable from a ticker.
            _ => null,
        };
    }

    /// <summary>Stooq wants lowercase symbols with a market suffix, e.g. "aapl.us".</summary>
    private static string DefaultStooq(string symbol, string? exchange)
    {
        var lower = symbol.ToLowerInvariant();
        if (lower.Contains('.'))
        {
            return lower;
        }

        return exchange is null || UsExchanges.Contains(exchange) ? lower + ".us" : lower;
    }
}
