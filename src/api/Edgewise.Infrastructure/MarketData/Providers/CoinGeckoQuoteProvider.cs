using System.Text.Json;

namespace Edgewise.Infrastructure.MarketData.Providers;

/// <summary>
/// CoinGecko simple/price adapter (USD quotes). Requires an explicit CoinGecko id
/// in ProviderSymbolsJson under key "coingecko" (e.g. "bitcoin") — ids are not
/// derivable from tickers.
/// </summary>
public sealed class CoinGeckoQuoteProvider(ProviderHttp http) : IQuoteProvider
{
    public string Name => ProviderNames.CoinGecko;

    public async Task<decimal> GetQuoteAsync(string providerSymbol, CancellationToken ct)
    {
        var id = providerSymbol.ToLowerInvariant();
        var url = $"https://api.coingecko.com/api/v3/simple/price?ids={Uri.EscapeDataString(id)}&vs_currencies=usd";
        var body = await http.GetStringAsync(Name, url, ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty(id, out var coin) && coin.TryGetProperty("usd", out var usd))
        {
            return usd.GetDecimal();
        }

        throw new FormatException($"CoinGecko has no USD price for id '{id}'.");
    }
}
