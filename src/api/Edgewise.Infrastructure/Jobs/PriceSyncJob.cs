using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.MarketData;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Jobs;

/// <summary>
/// Keeps quotes (every 15 minutes) and recent closed bars (hourly) warm for every
/// instrument referenced by any holding or watchlist item. Runs without a user
/// context; user-owned tables are read with IgnoreQueryFilters inside the service.
/// </summary>
[AutomaticRetry(Attempts = 3)]
public sealed class PriceSyncJob(MarketDataService market, ILogger<PriceSyncJob> logger)
{
    private static readonly (Timeframe Timeframe, TimeSpan Lookback)[] BarTargets =
    [
        (Timeframe.D1, TimeSpan.FromDays(400)), // enough closed dailies for SMA200
        (Timeframe.H4, TimeSpan.FromDays(60)),
        (Timeframe.H1, TimeSpan.FromDays(14)),
    ];

    /// <summary>Every 15 minutes: refresh quotes for tracked instruments.</summary>
    public async Task SyncQuotesAsync(CancellationToken ct)
    {
        var instrumentIds = await market.GetTrackedInstrumentIdsAsync(ct);
        foreach (var instrumentId in instrumentIds)
        {
            try
            {
                await market.GetQuoteAsync(instrumentId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Quote sync failed for instrument {InstrumentId}.", instrumentId);
            }
        }
    }

    /// <summary>Hourly: gap-fill recent closed D1/H4/H1 bars for tracked instruments.</summary>
    public async Task SyncBarsAsync(CancellationToken ct)
    {
        var instrumentIds = await market.GetTrackedInstrumentIdsAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var instrumentId in instrumentIds)
        {
            foreach (var (timeframe, lookback) in BarTargets)
            {
                try
                {
                    await market.GetBarsAsync(instrumentId, timeframe, now - lookback, now, fetchTimeout: null, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(
                        ex, "Bar sync failed for instrument {InstrumentId} {Timeframe}.", instrumentId, timeframe);
                }
            }
        }
    }
}
