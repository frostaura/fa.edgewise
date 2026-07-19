using System.Globalization;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Infrastructure.Ingestion.Connectors;

/// <summary>
/// Syncs Polymarket trades into Fills and maintains BrierForecasts.
///
/// Mapping decisions:
///  - Instrument per market: Symbol = market slug (fallback: first 18 chars of
///    conditionId), Exchange "Polymarket", AssetClass Prediction, Currency "USD"
///    (fills settle in USDC ~ USD), ProviderSymbolsJson {"polymarket": conditionId}.
///  - price = probability in [0,1] stored as-is; qty = outcome shares; fee = 0
///    (the data API does not expose fees).
///  - SourceHash = sha256("polymarket:{txHash}:{conditionId}:{outcomeIndex}:{side}:{size}:{price}:{timestamp}")
///    — a tx can contain several fills, so the hash covers the full fill identity.
///  - Fills land as MatchStatus.Proposed for the match-or-confess inbox.
///  - One BrierForecast per (market, traded outcome). Question = "{title} — {outcome}";
///    PMarket = first seen entry price; PUser defaults to PMarket until the user edits.
///    The conditionId + outcome are embedded in RulesUrl
///    ("https://polymarket.com/market/{slug}?cid={conditionId}&outcome={outcome}") so the
///    resolution job can match forecasts back to markets without a schema change.
///  - Resolution: a redeemable position (or curPrice pinned to 0/1) resolves the
///    forecast; Outcome = did the traded outcome win; BrierScore = (pUser − o)².
/// </summary>
public sealed class PolymarketSyncService(PolymarketClient client)
{
    private const int PageSize = 500;
    private const int MaxRowsPerRun = 10_000;

    public async Task<int> SyncAsync(
        EdgewiseDbContext db, Account account, AccountSyncStats stats, CancellationToken ct)
    {
        var wallet = account.WalletAddress
            ?? throw new IntegrationException(
                "missing_wallet", "This Polymarket account has no wallet address. Reconnect it with your proxy wallet.");

        var existingHashes = new HashSet<string>(await db.Fills.IgnoreQueryFilters()
            .Where(f => f.AccountId == account.Id)
            .Select(f => f.SourceHash)
            .ToListAsync(ct));

        var instrumentCache = await LoadPolymarketInstrumentsAsync(db, ct);

        var offset = stats.PolymarketOffset ?? 0;
        var imported = 0;
        var fetched = 0;

        while (fetched < MaxRowsPerRun)
        {
            ct.ThrowIfCancellationRequested();
            var trades = await client.GetTradesAsync(wallet, PageSize, offset, ct);
            if (trades.Count == 0)
            {
                break;
            }

            foreach (var trade in trades)
            {
                var hash = SourceHasher.Sha256(
                    $"polymarket:{trade.TransactionHash}:{trade.ConditionId}:{trade.OutcomeIndex}:" +
                    $"{trade.Side}:{trade.Size.ToString(CultureInfo.InvariantCulture)}:" +
                    $"{trade.Price.ToString(CultureInfo.InvariantCulture)}:{trade.Timestamp}");
                if (!existingHashes.Add(hash))
                {
                    continue;
                }

                var instrument = await GetOrCreateInstrumentAsync(db, instrumentCache, trade, ct);

                db.Fills.Add(new Fill
                {
                    Id = Guid.NewGuid(),
                    AccountId = account.Id,
                    UserId = account.UserId,
                    InstrumentId = instrument.Id,
                    Side = string.Equals(trade.Side, "SELL", StringComparison.OrdinalIgnoreCase)
                        ? FillSide.Sell
                        : FillSide.Buy,
                    Qty = trade.Size,
                    Price = trade.Price,
                    FeeMinor = 0,
                    FeeCurrency = "USD",
                    At = DateTimeOffset.FromUnixTimeSeconds(trade.Timestamp).UtcDateTime,
                    Source = FillSource.Api,
                    SourceHash = hash,
                    RawPayloadJson = trade.RawJson,
                    MatchStatus = MatchStatus.Proposed,
                });
                imported++;

                await UpsertBrierForecastAsync(db, account.UserId, trade, ct);
            }

            fetched += trades.Count;
            offset += trades.Count;
            await db.SaveChangesAsync(ct);

            if (trades.Count < PageSize)
            {
                break;
            }
        }

        stats.PolymarketOffset = offset;

        await ResolveFromPositionsAsync(db, account.UserId, wallet, ct);
        await db.SaveChangesAsync(ct);
        return imported;
    }

    // -------------------------------------------------------- instruments

    private static async Task<Dictionary<string, Instrument>> LoadPolymarketInstrumentsAsync(
        EdgewiseDbContext db, CancellationToken ct)
    {
        var instruments = await db.Instruments
            .Where(i => i.Exchange == "Polymarket")
            .ToListAsync(ct);

        var byConditionId = new Dictionary<string, Instrument>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments)
        {
            var conditionId = TryReadProviderSymbol(instrument.ProviderSymbolsJson, "polymarket");
            if (conditionId is not null)
            {
                byConditionId[conditionId] = instrument;
            }
        }

        return byConditionId;
    }

    private static async Task<Instrument> GetOrCreateInstrumentAsync(
        EdgewiseDbContext db, Dictionary<string, Instrument> cache, PolymarketTrade trade, CancellationToken ct)
    {
        if (cache.TryGetValue(trade.ConditionId, out var existing))
        {
            return existing;
        }

        var symbol = !string.IsNullOrWhiteSpace(trade.Slug)
            ? Truncate(trade.Slug!, 64)
            : Truncate(trade.ConditionId, 18);

        var instrument = new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Name = Truncate(trade.Title ?? trade.Slug ?? trade.ConditionId, 200),
            AssetClass = AssetClass.Prediction,
            Exchange = "Polymarket",
            Currency = "USD",
            ProviderSymbolsJson = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string> { ["polymarket"] = trade.ConditionId }),
        };
        db.Instruments.Add(instrument);
        await db.SaveChangesAsync(ct);
        cache[trade.ConditionId] = instrument;
        return instrument;
    }

    internal static string? TryReadProviderSymbol(string? providerSymbolsJson, string provider)
    {
        if (string.IsNullOrWhiteSpace(providerSymbolsJson))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(providerSymbolsJson);
            return doc.RootElement.TryGetProperty(provider, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------- brier

    private static async Task UpsertBrierForecastAsync(
        EdgewiseDbContext db, Guid userId, PolymarketTrade trade, CancellationToken ct)
    {
        var marker = ForecastMarker(trade.ConditionId, trade.Outcome);

        var tracked = db.ChangeTracker.Entries<BrierForecast>()
            .Select(e => e.Entity)
            .FirstOrDefault(f => f.UserId == userId && f.RulesUrl == marker);
        var existing = tracked ?? await db.BrierForecasts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.UserId == userId && f.RulesUrl == marker, ct);
        if (existing is not null)
        {
            return;
        }

        db.BrierForecasts.Add(new BrierForecast
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Question = Truncate($"{trade.Title ?? trade.Slug ?? trade.ConditionId} — {trade.Outcome ?? "Yes"}", 400),
            // The trade payload has no end date; default to +30d — the resolution
            // job refreshes it from the gamma markets API.
            ResolutionDate = DateTimeOffset.FromUnixTimeSeconds(trade.Timestamp).UtcDateTime.AddDays(30),
            RulesUrl = marker,
            PMarket = trade.Price,
            PUser = trade.Price,
        });
    }

    /// <summary>RulesUrl doubles as a stable market marker (documented mapping decision).</summary>
    internal static string ForecastMarker(string conditionId, string? outcome) =>
        $"https://polymarket.com/market?cid={conditionId}&outcome={Uri.EscapeDataString(outcome ?? "Yes")}";

    private async Task ResolveFromPositionsAsync(
        EdgewiseDbContext db, Guid userId, string wallet, CancellationToken ct)
    {
        IReadOnlyList<PolymarketPosition> positions;
        try
        {
            positions = await client.GetPositionsAsync(wallet, ct);
        }
        catch (IntegrationException)
        {
            return; // resolution is best-effort; trades already imported.
        }

        if (positions.Count == 0)
        {
            return;
        }

        var openForecasts = await db.BrierForecasts.IgnoreQueryFilters()
            .Where(f => f.UserId == userId && f.Outcome == null && f.RulesUrl != null
                && f.RulesUrl.StartsWith("https://polymarket.com/market?cid="))
            .ToListAsync(ct);
        if (openForecasts.Count == 0)
        {
            return;
        }

        foreach (var position in positions)
        {
            var resolved = position.Redeemable || position.CurPrice >= 0.999m || position.CurPrice <= 0.001m;
            if (!resolved)
            {
                continue;
            }

            var marker = ForecastMarker(position.ConditionId, position.Outcome);
            var forecast = openForecasts.FirstOrDefault(f => f.RulesUrl == marker);
            if (forecast is null)
            {
                continue;
            }

            // The traded outcome won when its terminal price pinned to 1
            // (a redeemable position on the winning side).
            var won = position.CurPrice >= 0.5m;
            forecast.Outcome = won;
            forecast.BrierScore = BrierScore(forecast.PUser, won);
            forecast.ResolvedAt = DateTime.UtcNow;
        }
    }

    internal static decimal BrierScore(decimal pUser, bool outcome)
    {
        var o = outcome ? 1m : 0m;
        return (pUser - o) * (pUser - o);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
