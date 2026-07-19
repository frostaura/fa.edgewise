using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.MarketData;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Jobs;

/// <summary>
/// Every 10 minutes: pulls the curated RSS feeds plus Finnhub company-news for
/// held/watched instruments, maps items to instruments by naive symbol/name
/// matching, and inserts with ON CONFLICT ("UrlHash") DO NOTHING for dedup.
/// </summary>
[AutomaticRetry(Attempts = 3)]
public sealed class NewsPollJob(
    EdgewiseDbContext db,
    MarketDataService market,
    INewsFeedProvider feeds,
    ICompanyNewsProvider companyNews,
    ProviderHealthWriter health,
    ILogger<NewsPollJob> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var trackedIds = await market.GetTrackedInstrumentIdsAsync(ct);
        var tracked = await db.Instruments
            .Where(i => trackedIds.Contains(i.Id))
            .ToListAsync(ct);

        // Broad feeds, mapped by naive text matching.
        try
        {
            var items = await feeds.GetLatestAsync(ct);
            foreach (var item in items)
            {
                var matched = MatchInstruments(item, tracked);
                await InsertAsync(item, matched, ct);
            }

            await health.ReportSuccessAsync(feeds.Name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "News feed poll failed.");
            await health.ReportErrorAsync(feeds.Name, ex.Message, ct);
        }

        // Per-instrument company news (no-op without FINNHUB_API_KEY).
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-2);
        foreach (var instrument in tracked.Where(i => i.AssetClass is AssetClass.Equity or AssetClass.Etf))
        {
            if (ProviderSymbols.Resolve(instrument, companyNews.Name) is not { } symbol)
            {
                continue;
            }

            try
            {
                foreach (var item in await companyNews.GetCompanyNewsAsync(symbol, from, to, ct))
                {
                    await InsertAsync(item, [instrument.Id], ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Company news poll failed for {Symbol}.", instrument.Symbol);
                await health.ReportErrorAsync(companyNews.Name, ex.Message, ct);
            }
        }
    }

    /// <summary>Naive relevance mapping: whole-word symbol match or case-insensitive name containment.</summary>
    internal static List<Guid> MatchInstruments(FetchedNewsItem item, IReadOnlyList<Instrument> instruments)
    {
        var text = item.Summary is null ? item.Title : $"{item.Title}\n{item.Summary}";
        var matched = new List<Guid>();
        foreach (var instrument in instruments)
        {
            if (ContainsWholeWord(text, instrument.Symbol)
                || (instrument.Name.Length >= 4 && text.Contains(instrument.Name, StringComparison.OrdinalIgnoreCase)))
            {
                matched.Add(instrument.Id);
            }
        }

        return matched;
    }

    private static bool ContainsWholeWord(string text, string word)
    {
        if (word.Length < 2)
        {
            return false;
        }

        var index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + word.Length;
            var afterOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            index = afterIndex;
        }

        return false;
    }

    private async Task InsertAsync(FetchedNewsItem item, List<Guid> instrumentIds, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var urlHash = HashUrl(item.Url);
        var publishedAt = item.PublishedAt.Kind == DateTimeKind.Utc
            ? item.PublishedAt
            : DateTime.SpecifyKind(item.PublishedAt, DateTimeKind.Utc);
        var instrumentIdsJson = instrumentIds.Count == 0
            ? null
            : JsonSerializer.Serialize(instrumentIds.Select(g => g.ToString()));
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "NewsItems" ("Id", "UrlHash", "Title", "Url", "Source", "PublishedAt", "Summary", "InstrumentIdsJson")
            VALUES ({id}, {urlHash}, {item.Title}, {item.Url}, {item.Source}, {publishedAt}, {item.Summary},
                CAST({instrumentIdsJson} AS jsonb))
            ON CONFLICT ("UrlHash") DO NOTHING
            """,
            ct);
    }

    /// <summary>Stable dedup key: lowercase hex SHA-256 of the trimmed URL.</summary>
    internal static string HashUrl(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())));
}
