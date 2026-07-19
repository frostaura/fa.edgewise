using System.Globalization;
using System.Text.Json;
using Edgewise.Domain.Entities;

namespace Edgewise.Infrastructure.MarketData.Providers;

/// <summary>
/// Coinbase Exchange public API adapter: /products/{id}/candles (granularity 3600
/// or 86400, max 300 rows per request — the range is chunked) and
/// /products/{id}/ticker for quotes. H4 and W1 are aggregated client-side.
/// Product ids like "BTC-USD".
/// </summary>
public sealed class CoinbaseCandlesProvider(ProviderHttp http) : IBarProvider, IQuoteProvider
{
    private const string BaseUrl = "https://api.exchange.coinbase.com/products";
    private const int MaxRowsPerRequest = 300;

    public string Name => ProviderNames.Coinbase;

    public async Task<IReadOnlyList<ProviderBar>> GetBarsAsync(
        string providerSymbol, Timeframe timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var (granularitySeconds, aggregateTo) = timeframe switch
        {
            Timeframe.H1 => (3600, (Timeframe?)null),
            Timeframe.H4 => (3600, Timeframe.H4),
            Timeframe.D1 => (86400, (Timeframe?)null),
            Timeframe.W1 => (86400, Timeframe.W1),
            _ => (0, null),
        };
        if (granularitySeconds == 0)
        {
            return [];
        }

        var step = TimeSpan.FromSeconds((double)granularitySeconds * MaxRowsPerRequest);
        var bars = new List<ProviderBar>();
        var cursor = fromUtc;
        for (var page = 0; page < 12 && cursor < toUtc; page++)
        {
            var chunkEnd = cursor + step < toUtc ? cursor + step : toUtc;
            var url = $"{BaseUrl}/{Uri.EscapeDataString(providerSymbol)}/candles" +
                $"?granularity={granularitySeconds}&start={Iso(cursor)}&end={Iso(chunkEnd)}";
            var body = await http.GetStringAsync(Name, url, ct);
            using var doc = JsonDocument.Parse(body);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                // [ time, low, high, open, close, volume ] — newest first.
                var ts = DateTimeOffset.FromUnixTimeSeconds(row[0].GetInt64()).UtcDateTime;
                bars.Add(new ProviderBar(
                    ts,
                    O: ParseDecimal(row[3]),
                    H: ParseDecimal(row[2]),
                    L: ParseDecimal(row[1]),
                    C: ParseDecimal(row[4]),
                    V: ParseDecimal(row[5])));
            }

            cursor = chunkEnd;
        }

        bars.Sort(static (a, b) => a.Ts.CompareTo(b.Ts));
        return aggregateTo is { } target ? TimeframeMath.Aggregate(bars, target) : bars;
    }

    public async Task<decimal> GetQuoteAsync(string providerSymbol, CancellationToken ct)
    {
        var url = $"{BaseUrl}/{Uri.EscapeDataString(providerSymbol)}/ticker";
        var body = await http.GetStringAsync(Name, url, ct);
        using var doc = JsonDocument.Parse(body);
        return ParseDecimal(doc.RootElement.GetProperty("price"));
    }

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number
            ? element.GetDecimal()
            : decimal.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
