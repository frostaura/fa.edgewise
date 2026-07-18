using System.Globalization;
using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Infrastructure.Ingestion.Connectors;

/// <summary>
/// Syncs Coinbase Advanced Trade fills into Fills.
///
/// Mapping decisions:
///  - GET /api/v3/brokerage/orders/historical/fills, cursor-paginated (limit 250).
///    Incremental runs pass start_sequence_timestamp = the newest
///    sequence_timestamp already ingested (persisted in LastSyncStatsJson);
///    SourceHash dedupe makes any overlap harmless.
///  - Instrument = product_id (e.g. BTC-USD): Symbol = product_id, Exchange
///    "Coinbase", AssetClass Crypto, Currency = quote leg,
///    ProviderSymbolsJson {"coinbase": product_id}.
///  - size_in_quote fills convert qty = size / price (best-effort; noted in the
///    wrapped RawPayloadJson).
///  - Fee: commission is quoted in the quote currency; USD-quoted products store
///    FeeMinor = cents, otherwise FeeMinor 0 with the commission left in raw payload.
///  - SourceHash = sha256("coinbase:{entry_id}"); fills land Proposed.
/// </summary>
public sealed class CoinbaseSyncService(CoinbaseClient client, EnvelopeCrypto crypto)
{
    private const int PageSize = 250;
    private const int MaxPagesPerRun = 40;

    public async Task<int> SyncAsync(
        EdgewiseDbContext db, Account account, AccountSyncStats stats, CancellationToken ct)
    {
        var creds = DecryptCredentials(account);

        var existingHashes = new HashSet<string>(await db.Fills.IgnoreQueryFilters()
            .Where(f => f.AccountId == account.Id)
            .Select(f => f.SourceHash)
            .ToListAsync(ct));

        var instrumentCache = await LoadCoinbaseInstrumentsAsync(db, ct);
        var imported = 0;
        string? cursor = null;
        var maxSequence = stats.CoinbaseLastSequenceTimestamp;

        for (var page = 0; page < MaxPagesPerRun; page++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await client.GetFillsAsync(
                creds.KeyName, creds.PrivateKeyPem, cursor, stats.CoinbaseLastSequenceTimestamp, PageSize, ct);

            foreach (var fill in result.Fills)
            {
                if (string.CompareOrdinal(fill.SequenceTimestamp, maxSequence) > 0)
                {
                    maxSequence = fill.SequenceTimestamp;
                }

                var hash = SourceHasher.Sha256($"coinbase:{fill.EntryId}");
                if (!existingHashes.Add(hash))
                {
                    continue;
                }

                var instrument = await GetOrCreateInstrumentAsync(db, instrumentCache, fill.ProductId, ct);
                var price = ParseDecimal(fill.Price);
                var size = ParseDecimal(fill.Size);
                var qty = fill.SizeInQuote && price > 0 ? size / price : size;

                db.Fills.Add(new Fill
                {
                    Id = Guid.NewGuid(),
                    AccountId = account.Id,
                    UserId = account.UserId,
                    InstrumentId = instrument.Id,
                    Side = string.Equals(fill.Side, "SELL", StringComparison.OrdinalIgnoreCase)
                        ? FillSide.Sell
                        : FillSide.Buy,
                    Qty = qty,
                    Price = price,
                    FeeMinor = MapFeeMinor(fill.Commission, QuoteCurrency(fill.ProductId)),
                    FeeCurrency = QuoteCurrency(fill.ProductId) == "USD" ? "USD" : QuoteCurrency(fill.ProductId),
                    At = DateTime.SpecifyKind(fill.TradeTime, DateTimeKind.Utc),
                    Source = FillSource.Api,
                    SourceHash = hash,
                    RawPayloadJson = fill.SizeInQuote
                        ? $"{{\"fill\":{fill.RawJson},\"note\":\"size_in_quote converted to base qty\"}}"
                        : fill.RawJson,
                    MatchStatus = MatchStatus.Proposed,
                });
                imported++;
            }

            await db.SaveChangesAsync(ct);
            cursor = result.Cursor;
            if (cursor is null || result.Fills.Count == 0)
            {
                break;
            }
        }

        stats.CoinbaseLastSequenceTimestamp = maxSequence;
        return imported;
    }

    public CoinbaseCredentials DecryptCredentials(Account account)
    {
        if (string.IsNullOrEmpty(account.CredentialsEnc))
        {
            throw new IntegrationException(
                "missing_credentials", "This Coinbase account has no stored API key — reconnect it.");
        }

        var creds = JsonSerializer.Deserialize<CoinbaseCredentials>(
            crypto.DecryptToString(account.CredentialsEnc), IntegrationJson.Options);
        if (creds is null || string.IsNullOrEmpty(creds.KeyName) || string.IsNullOrEmpty(creds.PrivateKeyPem))
        {
            throw new IntegrationException(
                "missing_credentials", "Stored Coinbase credentials are incomplete — reconnect the account.");
        }

        return creds;
    }

    // --------------------------------------------------------- instruments

    private static async Task<Dictionary<string, Instrument>> LoadCoinbaseInstrumentsAsync(
        EdgewiseDbContext db, CancellationToken ct)
    {
        var instruments = await db.Instruments.Where(i => i.Exchange == "Coinbase").ToListAsync(ct);
        var byProduct = new Dictionary<string, Instrument>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var provider = PolymarketSyncService.TryReadProviderSymbol(instrument.ProviderSymbolsJson, "coinbase");
            byProduct[provider ?? instrument.Symbol] = instrument;
        }

        return byProduct;
    }

    private static async Task<Instrument> GetOrCreateInstrumentAsync(
        EdgewiseDbContext db, Dictionary<string, Instrument> cache, string productId, CancellationToken ct)
    {
        if (cache.TryGetValue(productId, out var existing))
        {
            return existing;
        }

        var instrument = new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = productId,
            Name = $"{productId} (Coinbase)",
            AssetClass = AssetClass.Crypto,
            Exchange = "Coinbase",
            Currency = QuoteCurrency(productId),
            ProviderSymbolsJson = JsonSerializer.Serialize(
                new Dictionary<string, string> { ["coinbase"] = productId }),
        };
        db.Instruments.Add(instrument);
        await db.SaveChangesAsync(ct);
        cache[productId] = instrument;
        return instrument;
    }

    internal static string QuoteCurrency(string productId)
    {
        var index = productId.LastIndexOf('-');
        var quote = index >= 0 && index < productId.Length - 1 ? productId[(index + 1)..] : "USD";
        return quote is "USDC" or "USDT" ? "USD" : quote;
    }

    internal static long MapFeeMinor(string commission, string quoteCurrency)
    {
        if (quoteCurrency != "USD")
        {
            return 0;
        }

        var amount = ParseDecimal(commission);
        return (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero);
    }

    private static decimal ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
}
