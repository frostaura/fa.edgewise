using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.MarketData;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Jobs;

/// <summary>
/// Twice daily: pulls Finnhub earnings for held/watched equities plus red/amber
/// macro prints and upserts global CatalystEvents keyed by SourceRef. A silent
/// no-op when FINNHUB_API_KEY is not configured.
/// </summary>
[AutomaticRetry(Attempts = 3)]
public sealed class CalendarSyncJob(
    EdgewiseDbContext db,
    MarketDataService market,
    ICalendarProvider calendar,
    ProviderHealthWriter health,
    ILogger<CalendarSyncJob> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(DateTime.UtcNow);
        var to = from.AddDays(30);

        var trackedIds = await market.GetTrackedInstrumentIdsAsync(ct);
        var trackedEquities = await db.Instruments
            .Where(i => trackedIds.Contains(i.Id)
                && (i.AssetClass == AssetClass.Equity || i.AssetClass == AssetClass.Etf))
            .ToListAsync(ct);
        var bySymbol = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in trackedEquities)
        {
            if (ProviderSymbols.Resolve(instrument, calendar.Name) is { } symbol)
            {
                bySymbol[symbol] = instrument.Id;
            }
        }

        try
        {
            var earnings = await calendar.GetEarningsAsync(from, to, ct);
            foreach (var fetched in earnings)
            {
                // Only earnings for instruments the users actually track.
                if (fetched.Symbol is not null && bySymbol.TryGetValue(fetched.Symbol, out var instrumentId))
                {
                    await UpsertAsync(fetched, instrumentId, ct);
                }
            }

            foreach (var fetched in await calendar.GetEconomicEventsAsync(from, to, ct))
            {
                await UpsertAsync(fetched, instrumentId: null, ct);
            }

            await db.SaveChangesAsync(ct);
            await health.ReportSuccessAsync(calendar.Name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Calendar sync failed.");
            await health.ReportErrorAsync(calendar.Name, ex.Message, ct);
            throw; // let Hangfire retry
        }
    }

    private async Task UpsertAsync(FetchedCalendarEvent fetched, Guid? instrumentId, CancellationToken ct)
    {
        var existing = await db.CatalystEvents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.SourceRef == fetched.SourceRef && c.UserId == null, ct);
        if (existing is null)
        {
            db.CatalystEvents.Add(new CatalystEvent
            {
                Id = Guid.NewGuid(),
                Kind = fetched.Kind,
                InstrumentId = instrumentId,
                UserId = null,
                Title = fetched.Title,
                At = fetched.At,
                Severity = fetched.Severity,
                SourceRef = fetched.SourceRef,
            });
        }
        else
        {
            existing.Title = fetched.Title;
            existing.At = fetched.At;
            existing.Severity = fetched.Severity;
            existing.InstrumentId = instrumentId ?? existing.InstrumentId;
        }
    }
}
