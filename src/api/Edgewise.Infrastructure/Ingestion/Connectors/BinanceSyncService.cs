using System.Globalization;
using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Infrastructure.Ingestion.Connectors;

/// <summary>Decrypted credential shapes stored in Account.CredentialsEnc (via EnvelopeCrypto only).</summary>
public sealed record BinanceCredentials(string ApiKey, string ApiSecret);

public sealed record CoinbaseCredentials(string KeyName, string PrivateKeyPem);

/// <summary>
/// Syncs Binance spot trade history into Fills.
///
/// Mapping decisions:
///  - Symbol discovery: nonzero balances from /api/v3/account plus the user's
///    held/watched Crypto instruments that carry a {"binance": "..."} provider
///    symbol; candidates = providerSymbols ∪ {ASSET+USDT, ASSET+USDC}, validated
///    against /api/v3/exchangeInfo TRADING symbols.
///  - Backfill queue is resumable: progress persists in Account.LastSyncStatsJson
///    (symbolsTotal / symbolsDone / currentSymbol / lastTradeIdPerSymbol) after
///    every symbol, so an interrupted run continues where it stopped and the UI
///    can show x/y progress. Incremental runs page /api/v3/myTrades with
///    fromId = lastTradeId + 1 (limit 1000).
///  - Instrument: reuse any Exchange=="Binance" instrument whose provider symbol
///    matches (e.g. seeded BTC-USD ↔ BTCUSDT); otherwise create Symbol = raw
///    Binance symbol, AssetClass Crypto, Currency = quote asset (stables map to USD).
///  - Fees: commission in USD/USDT/USDC/BUSD/FDUSD/TUSD → FeeMinor cents with
///    FeeCurrency "USD" (best-effort 1:1); any other commission asset → FeeMinor 0
///    and a feeNote wrapped into RawPayloadJson.
///  - SourceHash = sha256("binance:{symbol}:{tradeId}"); fills land Proposed.
/// </summary>
public sealed class BinanceSyncService(BinanceClient client, EnvelopeCrypto crypto)
{
    private static readonly string[] StableQuotes = ["USDT", "USDC", "BUSD", "FDUSD", "TUSD", "USD"];

    public async Task<int> SyncAsync(
        EdgewiseDbContext db, Account account, AccountSyncStats stats, CancellationToken ct)
    {
        var creds = DecryptCredentials(account);

        var symbols = await DiscoverSymbolsAsync(db, account, creds, ct);
        stats.SymbolsTotal = symbols.Count;
        stats.SymbolsDone = 0;
        stats.LastTradeIdPerSymbol ??= new Dictionary<string, long>();

        var existingHashes = new HashSet<string>(await db.Fills.IgnoreQueryFilters()
            .Where(f => f.AccountId == account.Id)
            .Select(f => f.SourceHash)
            .ToListAsync(ct));

        var binanceInstruments = await LoadBinanceInstrumentsAsync(db, ct);
        var imported = 0;

        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();
            stats.CurrentSymbol = symbol.Symbol;
            PersistStats(account, stats);
            await db.SaveChangesAsync(ct);

            var fromId = stats.LastTradeIdPerSymbol.TryGetValue(symbol.Symbol, out var last) ? last + 1 : (long?)null;

            while (true)
            {
                var trades = await client.GetMyTradesAsync(
                    creds.ApiKey, creds.ApiSecret, symbol.Symbol, fromId, 1000, ct);
                if (trades.Count == 0)
                {
                    break;
                }

                var instrument = await GetOrCreateInstrumentAsync(db, binanceInstruments, symbol, ct);

                foreach (var trade in trades)
                {
                    var hash = SourceHasher.Sha256($"binance:{symbol.Symbol}:{trade.Id}");
                    stats.LastTradeIdPerSymbol[symbol.Symbol] =
                        Math.Max(trade.Id, stats.LastTradeIdPerSymbol.GetValueOrDefault(symbol.Symbol));
                    if (!existingHashes.Add(hash))
                    {
                        continue;
                    }

                    var (feeMinor, feeCurrency, feeNote) = MapFee(trade.Commission, trade.CommissionAsset);
                    db.Fills.Add(new Fill
                    {
                        Id = Guid.NewGuid(),
                        AccountId = account.Id,
                        UserId = account.UserId,
                        InstrumentId = instrument.Id,
                        Side = trade.IsBuyer ? FillSide.Buy : FillSide.Sell,
                        Qty = ParseDecimal(trade.Qty),
                        Price = ParseDecimal(trade.Price),
                        FeeMinor = feeMinor,
                        FeeCurrency = feeCurrency,
                        At = DateTimeOffset.FromUnixTimeMilliseconds(trade.TimeMs).UtcDateTime,
                        Source = FillSource.Api,
                        SourceHash = hash,
                        RawPayloadJson = feeNote is null
                            ? trade.RawJson
                            : $"{{\"trade\":{trade.RawJson},\"feeNote\":{JsonSerializer.Serialize(feeNote)}}}",
                        MatchStatus = MatchStatus.Proposed,
                    });
                    imported++;
                }

                fromId = stats.LastTradeIdPerSymbol[symbol.Symbol] + 1;
                if (trades.Count < 1000)
                {
                    break;
                }
            }

            stats.SymbolsDone++;
            stats.CurrentSymbol = null;
            PersistStats(account, stats);
            await db.SaveChangesAsync(ct);
        }

        return imported;
    }

    public BinanceCredentials DecryptCredentials(Account account)
    {
        if (string.IsNullOrEmpty(account.CredentialsEnc))
        {
            throw new IntegrationException(
                "missing_credentials", "This Binance account has no stored API key — reconnect it.");
        }

        var creds = JsonSerializer.Deserialize<BinanceCredentials>(
            crypto.DecryptToString(account.CredentialsEnc), IntegrationJson.Options);
        if (creds is null || string.IsNullOrEmpty(creds.ApiKey) || string.IsNullOrEmpty(creds.ApiSecret))
        {
            throw new IntegrationException(
                "missing_credentials", "Stored Binance credentials are incomplete — reconnect the account.");
        }

        return creds;
    }

    private static void PersistStats(Account account, AccountSyncStats stats) =>
        account.LastSyncStatsJson = stats.ToJson();

    // ---------------------------------------------------------- discovery

    private async Task<IReadOnlyList<BinanceSymbolInfo>> DiscoverSymbolsAsync(
        EdgewiseDbContext db, Account account, BinanceCredentials creds, CancellationToken ct)
    {
        var assets = await client.GetNonZeroBalanceAssetsAsync(creds.ApiKey, creds.ApiSecret, ct);
        var exchangeSymbols = await client.GetExchangeSymbolsAsync(ct);

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            candidates.Add($"{asset}USDT");
            candidates.Add($"{asset}USDC");
        }

        // Held or watched crypto instruments that already carry a Binance symbol.
        var userInstrumentIds = (await db.Holdings.IgnoreQueryFilters()
                .Where(h => h.UserId == account.UserId)
                .Select(h => h.InstrumentId)
                .ToListAsync(ct))
            .Concat(await db.WatchlistItems.IgnoreQueryFilters()
                .Where(w => w.Watchlist.UserId == account.UserId)
                .Select(w => w.InstrumentId)
                .ToListAsync(ct))
            .ToHashSet();

        if (userInstrumentIds.Count > 0)
        {
            var cryptoInstruments = await db.Instruments
                .Where(i => userInstrumentIds.Contains(i.Id) && i.AssetClass == AssetClass.Crypto)
                .ToListAsync(ct);
            foreach (var instrument in cryptoInstruments)
            {
                var providerSymbol =
                    PolymarketSyncService.TryReadProviderSymbol(instrument.ProviderSymbolsJson, "binance");
                if (providerSymbol is not null)
                {
                    candidates.Add(providerSymbol);
                }
            }
        }

        return candidates
            .Where(exchangeSymbols.ContainsKey)
            .Select(c => exchangeSymbols[c])
            .OrderBy(s => s.Symbol, StringComparer.Ordinal)
            .ToList();
    }

    // --------------------------------------------------------- instruments

    private static async Task<Dictionary<string, Instrument>> LoadBinanceInstrumentsAsync(
        EdgewiseDbContext db, CancellationToken ct)
    {
        var instruments = await db.Instruments.Where(i => i.Exchange == "Binance").ToListAsync(ct);
        var bySymbol = new Dictionary<string, Instrument>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var provider = PolymarketSyncService.TryReadProviderSymbol(instrument.ProviderSymbolsJson, "binance");
            bySymbol[provider ?? instrument.Symbol] = instrument;
        }

        return bySymbol;
    }

    private static async Task<Instrument> GetOrCreateInstrumentAsync(
        EdgewiseDbContext db, Dictionary<string, Instrument> cache, BinanceSymbolInfo symbol, CancellationToken ct)
    {
        if (cache.TryGetValue(symbol.Symbol, out var existing))
        {
            return existing;
        }

        var instrument = new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = symbol.Symbol,
            Name = $"{symbol.BaseAsset}/{symbol.QuoteAsset} (Binance)",
            AssetClass = AssetClass.Crypto,
            Exchange = "Binance",
            Currency = StableQuotes.Contains(symbol.QuoteAsset, StringComparer.OrdinalIgnoreCase)
                ? "USD"
                : symbol.QuoteAsset,
            ProviderSymbolsJson = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["binance"] = symbol.Symbol }),
        };
        db.Instruments.Add(instrument);
        await db.SaveChangesAsync(ct);
        cache[symbol.Symbol] = instrument;
        return instrument;
    }

    // ---------------------------------------------------------------- fees

    internal static (long FeeMinor, string FeeCurrency, string? Note) MapFee(string commission, string asset)
    {
        var amount = ParseDecimal(commission);
        if (amount == 0)
        {
            return (0, "USD", null);
        }

        if (StableQuotes.Contains(asset, StringComparer.OrdinalIgnoreCase))
        {
            return ((long)Math.Round(amount * 100, MidpointRounding.AwayFromZero), "USD", null);
        }

        return (0, asset, $"commission {commission} {asset} not converted to USD");
    }

    private static decimal ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
}
