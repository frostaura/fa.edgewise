using System.Globalization;
using Edgewise.Domain.Entities;

namespace Edgewise.Infrastructure.MarketData.Providers;

/// <summary>
/// Stooq CSV adapter. Daily (and weekly) history via q/d/l; latest close via the
/// lightweight q/l endpoint. Intraday timeframes are not supported and return empty.
/// Symbols use Stooq notation, e.g. "aapl.us" (see <see cref="ProviderSymbols"/>).
/// </summary>
public sealed class StooqProvider(ProviderHttp http) : IBarProvider, IQuoteProvider
{
    public string Name => ProviderNames.Stooq;

    public async Task<IReadOnlyList<ProviderBar>> GetBarsAsync(
        string providerSymbol, Timeframe timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var interval = timeframe switch
        {
            Timeframe.D1 => "d",
            Timeframe.W1 => "w",
            _ => null,
        };
        if (interval is null)
        {
            return [];
        }

        var url = $"https://stooq.com/q/d/l/?s={Uri.EscapeDataString(providerSymbol)}&i={interval}" +
            $"&d1={fromUtc:yyyyMMdd}&d2={toUtc:yyyyMMdd}";
        var csv = await http.GetStringAsync(Name, url, ct);
        return ParseDailyCsv(csv);
    }

    public async Task<decimal> GetQuoteAsync(string providerSymbol, CancellationToken ct)
    {
        // Symbol,Date,Time,Open,High,Low,Close,Volume
        var url = $"https://stooq.com/q/l/?s={Uri.EscapeDataString(providerSymbol)}&f=sd2t2ohlcv&h&e=csv";
        var csv = await http.GetStringAsync(Name, url, ct);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
        {
            throw new FormatException("Stooq quote response is empty.");
        }

        var fields = lines[1].Split(',');
        if (fields.Length < 7 || !TryDecimal(fields[6], out var close))
        {
            throw new FormatException($"Stooq has no quote for '{providerSymbol}'.");
        }

        return close;
    }

    private static List<ProviderBar> ParseDailyCsv(string csv)
    {
        // Date,Open,High,Low,Close,Volume
        var bars = new List<ProviderBar>();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split(',');
            if (fields.Length < 5
                || !DateTime.TryParseExact(
                    fields[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
                || !TryDecimal(fields[1], out var o) || !TryDecimal(fields[2], out var h)
                || !TryDecimal(fields[3], out var l) || !TryDecimal(fields[4], out var c))
            {
                continue;
            }

            bars.Add(new ProviderBar(date, o, h, l, c, fields.Length > 5 ? ParseVolume(fields[5]) : 0m));
        }

        return bars;
    }

    private static decimal ParseVolume(string field) =>
        TryDecimal(field, out var volume) ? volume : 0m;

    private static bool TryDecimal(string field, out decimal value) =>
        decimal.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
