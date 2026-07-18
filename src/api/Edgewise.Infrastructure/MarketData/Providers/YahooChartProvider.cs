using System.Globalization;
using System.Text.Json;
using Edgewise.Domain.Entities;

namespace Edgewise.Infrastructure.MarketData.Providers;

/// <summary>
/// Yahoo Finance v8 chart API adapter (bars + quotes). H4 is aggregated client-side
/// from 1h candles since Yahoo has no native 4-hour interval.
/// </summary>
public sealed class YahooChartProvider(ProviderHttp http) : IBarProvider, IQuoteProvider
{
    private const string BaseUrl = "https://query1.finance.yahoo.com/v8/finance/chart";

    public string Name => ProviderNames.Yahoo;

    public async Task<IReadOnlyList<ProviderBar>> GetBarsAsync(
        string providerSymbol, Timeframe timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var interval = timeframe switch
        {
            Timeframe.H1 or Timeframe.H4 => "1h",
            Timeframe.D1 => "1d",
            Timeframe.W1 => "1wk",
            _ => null,
        };
        if (interval is null)
        {
            return [];
        }

        // Yahoo limits 1h data to ~730 days; clamp to keep the request valid.
        if (interval == "1h" && fromUtc < DateTime.UtcNow.AddDays(-720))
        {
            fromUtc = DateTime.UtcNow.AddDays(-720);
        }

        var period1 = ToUnix(fromUtc);
        var period2 = ToUnix(toUtc);
        var url = $"{BaseUrl}/{Uri.EscapeDataString(providerSymbol)}" +
            $"?period1={period1}&period2={period2}&interval={interval}&includePrePost=false";
        var body = await http.GetStringAsync(Name, url, ct);
        var bars = ParseChartBars(body);
        return timeframe == Timeframe.H4 ? TimeframeMath.Aggregate(bars, Timeframe.H4) : bars;
    }

    public async Task<decimal> GetQuoteAsync(string providerSymbol, CancellationToken ct)
    {
        var url = $"{BaseUrl}/{Uri.EscapeDataString(providerSymbol)}?range=1d&interval=1d";
        var body = await http.GetStringAsync(Name, url, ct);
        using var doc = JsonDocument.Parse(body);
        var meta = FirstResult(doc).GetProperty("meta");
        if (meta.TryGetProperty("regularMarketPrice", out var price) && price.ValueKind == JsonValueKind.Number)
        {
            return price.GetDecimal();
        }

        throw new FormatException("Yahoo chart response has no regularMarketPrice.");
    }

    private static List<ProviderBar> ParseChartBars(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = FirstResult(doc);
        if (!result.TryGetProperty("timestamp", out var timestamps) || timestamps.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var quote = result.GetProperty("indicators").GetProperty("quote")[0];
        var opens = quote.GetProperty("open");
        var highs = quote.GetProperty("high");
        var lows = quote.GetProperty("low");
        var closes = quote.GetProperty("close");
        var volumes = quote.GetProperty("volume");

        var bars = new List<ProviderBar>(timestamps.GetArrayLength());
        for (var i = 0; i < timestamps.GetArrayLength(); i++)
        {
            if (TryDecimal(opens[i], out var o) && TryDecimal(highs[i], out var h)
                && TryDecimal(lows[i], out var l) && TryDecimal(closes[i], out var c))
            {
                TryDecimal(volumes[i], out var v);
                var ts = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime;
                bars.Add(new ProviderBar(ts, o, h, l, c, v));
            }
        }

        return bars;
    }

    private static JsonElement FirstResult(JsonDocument doc)
    {
        var chart = doc.RootElement.GetProperty("chart");
        if (chart.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw new FormatException(
                $"Yahoo chart error: {error.GetProperty("code").GetString()}");
        }

        var results = chart.GetProperty("result");
        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            throw new FormatException("Yahoo chart response has no result.");
        }

        return results[0];
    }

    private static bool TryDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            // Yahoo emits doubles; round-trip through double keeps parsing lenient.
            value = decimal.Parse(
                element.GetDouble().ToString("R", CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture);
            return true;
        }

        value = 0m;
        return false;
    }

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
