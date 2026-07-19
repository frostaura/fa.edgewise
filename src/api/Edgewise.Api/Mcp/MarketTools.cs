using System.ComponentModel;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.MarketData;
using ModelContextProtocol.Server;

namespace Edgewise.Api.Mcp;

/// <summary>
/// Market data tools over the shared instrument catalogue: latest quotes and
/// historical OHLC bars (served from storage, fetching only the missing tail
/// from the provider chain within a small inline budget).
/// </summary>
[McpServerToolType]
public sealed class MarketTools(EdgewiseDbContext db, MarketDataService market)
{
    /// <summary>Bounds the inline provider fetch so a slow provider cannot stall the tool call.</summary>
    private static readonly TimeSpan InlineFetchBudget = TimeSpan.FromSeconds(5);

    private const int MaxBars = 500;

    [McpServerTool(Name = "market_get_quote")]
    [Description("Latest price for an instrument symbol. 'stale' true means every provider failed and the last " +
        "cached price (ageMinutes old) is returned instead of a fresh one.")]
    public Task<string> GetQuote(
        [Description("Instrument symbol from the catalogue, e.g. 'BTC-USD' or 'STX500.JO'.")] string symbol,
        CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
    {
        var instrument = await McpToolSupport.ResolveInstrumentAsync(db, symbol, ct);
        var quote = await market.GetQuoteAsync(instrument.Id, ct);
        if (quote is null)
        {
            return new
            {
                error = "quote_unavailable",
                message = $"No price for '{instrument.Symbol}' is available right now: every provider failed and " +
                    "nothing is cached. Try again later or check the instrument's provider mappings.",
            };
        }

        return (object)new
        {
            symbol = instrument.Symbol,
            currency = instrument.Currency,
            quote.Price,
            quote.AsOf,
            quote.Provider,
            quote.Stale,
            quote.AgeMinutes,
        };
    });

    [McpServerTool(Name = "market_get_bars")]
    [Description("Historical OHLCV bars for an instrument as compact rows {ts,o,h,l,c,v}, oldest first. " +
        "'stale' true means the newest bars could not be refreshed in time and the tail may be missing.")]
    public Task<string> GetBars(
        [Description("Instrument symbol from the catalogue, e.g. 'BTC-USD'.")] string symbol,
        [Description("Bar timeframe: 'h1', 'h4', 'd1' or 'w1'. Default 'd1'.")] string timeframe = "d1",
        [Description("How many days back from now to include (1-1825). Default 30.")] int lookbackDays = 30,
        CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
    {
        var instrument = await McpToolSupport.ResolveInstrumentAsync(db, symbol, ct);
        var parsedTimeframe = McpToolSupport.ParseEnum<Timeframe>(timeframe, "timeframe");
        var days = Math.Clamp(lookbackDays, 1, 1825);
        var toUtc = DateTime.UtcNow;

        var result = await market.GetBarsAsync(
            instrument.Id, parsedTimeframe, toUtc.AddDays(-days), toUtc, InlineFetchBudget, ct);

        // Cap the payload at the newest MaxBars rows; bars stay oldest-first.
        var bars = result.Bars.Count > MaxBars ? result.Bars.Skip(result.Bars.Count - MaxBars).ToList() : result.Bars;

        return new
        {
            symbol = instrument.Symbol,
            timeframe = parsedTimeframe,
            lookbackDays = days,
            stale = result.Stale,
            count = bars.Count,
            truncated = result.Bars.Count > bars.Count ? true : (bool?)null,
            bars = bars.Select(b => new { ts = b.Ts, o = b.O, h = b.H, l = b.L, c = b.C, v = b.V }),
        };
    });
}
