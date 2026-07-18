using System.Globalization;
using System.Text.Json;
using Edgewise.Domain.Entities;

namespace Edgewise.Infrastructure.MarketData.Providers;

/// <summary>
/// Binance spot public API adapter: /api/v3/klines for bars (native 1h/4h/1d/1w)
/// and /api/v3/ticker/price for quotes. Symbols like "BTCUSDT".
/// </summary>
public sealed class BinanceKlinesProvider(ProviderHttp http) : IBarProvider, IQuoteProvider
{
    private const string BaseUrl = "https://api.binance.com/api/v3";

    public string Name => ProviderNames.Binance;

    public async Task<IReadOnlyList<ProviderBar>> GetBarsAsync(
        string providerSymbol, Timeframe timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var interval = timeframe switch
        {
            Timeframe.H1 => "1h",
            Timeframe.H4 => "4h",
            Timeframe.D1 => "1d",
            Timeframe.W1 => "1w",
            _ => null,
        };
        if (interval is null)
        {
            return [];
        }

        var bars = new List<ProviderBar>();
        var cursor = fromUtc;
        // Binance caps a klines request at 1000 rows; page forward until the range is covered.
        for (var page = 0; page < 10 && cursor < toUtc; page++)
        {
            var url = $"{BaseUrl}/klines?symbol={Uri.EscapeDataString(providerSymbol)}&interval={interval}" +
                $"&startTime={ToUnixMs(cursor)}&endTime={ToUnixMs(toUtc)}&limit=1000";
            var body = await http.GetStringAsync(Name, url, ct);
            using var doc = JsonDocument.Parse(body);
            var count = 0;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()).UtcDateTime;
                bars.Add(new ProviderBar(
                    ts,
                    ParseDecimal(row[1]),
                    ParseDecimal(row[2]),
                    ParseDecimal(row[3]),
                    ParseDecimal(row[4]),
                    ParseDecimal(row[5])));
                count++;
            }

            if (count < 1000)
            {
                break;
            }

            cursor = bars[^1].Ts + TimeframeMath.Duration(timeframe);
        }

        return bars;
    }

    public async Task<decimal> GetQuoteAsync(string providerSymbol, CancellationToken ct)
    {
        var url = $"{BaseUrl}/ticker/price?symbol={Uri.EscapeDataString(providerSymbol)}";
        var body = await http.GetStringAsync(Name, url, ct);
        using var doc = JsonDocument.Parse(body);
        return ParseDecimal(doc.RootElement.GetProperty("price"));
    }

    private static long ToUnixMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static decimal ParseDecimal(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number
            ? element.GetDecimal()
            : decimal.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
