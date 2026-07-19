using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Ingestion.Csv;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Journal;

/// <summary>
/// CSV import: preview (headers + suggested mapping + sample rows) and commit
/// (normalize → Fill rows landing as Proposed in the inbox). Idempotent: the
/// SourceHash is SHA256(venue + raw row), so re-importing the same file skips
/// duplicates via the (AccountId, SourceHash) unique index.
/// </summary>
public sealed class CsvImportService(EdgewiseDbContext db, ICurrentUser currentUser)
{
    public const long MaxFileBytes = 25 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ImportPreviewDto> PreviewAsync(Stream file, Venue? venueHint, CancellationToken ct)
    {
        var table = CsvTableReader.Read(file, maxRows: 20);
        if (table.Columns.Count == 0)
        {
            throw ApiException.BadRequest("empty_csv", "The file has no parseable header row.");
        }

        var (venue, mapping) = VenueMappingSuggestions.Suggest(table.Columns, venueHint);
        var saved = await db.ImportMappings.AsNoTracking()
            .OrderBy(m => m.Name)
            .Select(m => new ImportMappingDto(m.Id, m.Venue, m.Name, m.MappingJson))
            .ToListAsync(ct);

        return new ImportPreviewDto(
            table.Columns,
            mapping.ToDictionary(),
            venue,
            table.Rows.Take(20).ToList(),
            saved);
    }

    public async Task<ImportCommitResponse> CommitAsync(
        Stream file,
        Dictionary<string, string?> mappingDict,
        Guid accountId,
        Venue venue,
        string? saveMappingAs,
        CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);
        _ = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw ApiException.BadRequest("account_not_found", "Account not found.");

        var mapping = CsvFieldMapping.FromDictionary(mappingDict);
        var table = CsvTableReader.Read(file);
        var (rows, errors) = CsvFillNormalizer.Normalize(table, mapping);

        var existingHashes = (await db.Fills.AsNoTracking()
                .Where(f => f.AccountId == accountId)
                .Select(f => f.SourceHash)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var instrumentCache = new Dictionary<string, Instrument>(StringComparer.OrdinalIgnoreCase);
        var imported = 0;
        var duplicates = 0;

        foreach (var row in rows)
        {
            var hash = SourceHash(venue, row.RawRow);
            if (!existingHashes.Add(hash))
            {
                duplicates++;
                continue;
            }

            var instrument = await ResolveInstrumentAsync(row.Symbol, instrumentCache, ct);
            db.Fills.Add(new Fill
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                UserId = userId,
                InstrumentId = instrument.Id,
                TradeId = null,
                Side = row.IsBuy ? FillSide.Buy : FillSide.Sell,
                Qty = row.Qty,
                Price = row.Price,
                FeeMinor = JournalCommon.ToMinor(row.Fee),
                FeeCurrency = row.FeeCurrency ?? instrument.Currency,
                At = row.AtUtc,
                Source = FillSource.Csv,
                SourceHash = hash,
                RawPayloadJson = JsonSerializer.Serialize(new { venue = venue.ToString(), raw = row.RawRow }, Json),
                MatchStatus = MatchStatus.Proposed,
            });
            imported++;
        }

        if (!string.IsNullOrWhiteSpace(saveMappingAs))
        {
            var name = saveMappingAs.Trim();
            var existing = await db.ImportMappings.FirstOrDefaultAsync(m => m.Name == name && m.Venue == venue, ct);
            var mappingJson = JsonSerializer.Serialize(mapping.ToDictionary(), Json);
            if (existing is null)
            {
                db.ImportMappings.Add(new ImportMapping
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Venue = venue,
                    Name = name,
                    MappingJson = mappingJson,
                });
            }
            else
            {
                existing.MappingJson = mappingJson;
            }
        }

        await db.SaveChangesAsync(ct);
        return new ImportCommitResponse(imported, duplicates, errors);
    }

    public static string SourceHash(Venue venue, string rawRow) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{venue}|{rawRow}")));

    /// <summary>
    /// Resolves a venue symbol against the catalogue: exact symbol match first, then
    /// provider-symbol containment (e.g. Binance "BTCUSDT"), else creates a Custom instrument.
    /// The whole (small) catalogue is loaded once per commit and matched in memory.
    /// </summary>
    private async Task<Instrument> ResolveInstrumentAsync(
        string symbol, Dictionary<string, Instrument> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(symbol, out var cached))
        {
            return cached;
        }

        if (cache.Count == 0)
        {
            foreach (var known in await db.Instruments.ToListAsync(ct))
            {
                cache.TryAdd($"#catalogue:{known.Id}", known);
            }
        }

        var upper = symbol.Trim().ToUpperInvariant();
        var catalogue = cache.Values.ToList();
        var instrument = catalogue.FirstOrDefault(i => string.Equals(i.Symbol, upper, StringComparison.OrdinalIgnoreCase))
            ?? catalogue.FirstOrDefault(i => i.ProviderSymbolsJson is not null
                && i.ProviderSymbolsJson.Contains($"\"{upper}\"", StringComparison.OrdinalIgnoreCase));

        if (instrument is null)
        {
            instrument = new Instrument
            {
                Id = Guid.NewGuid(),
                Symbol = upper,
                Name = upper,
                AssetClass = AssetClass.Custom,
                Exchange = null,
                Currency = "USD",
                ProviderSymbolsJson = "{}",
            };
            db.Instruments.Add(instrument);
        }

        cache[symbol] = instrument;
        return instrument;
    }
}
