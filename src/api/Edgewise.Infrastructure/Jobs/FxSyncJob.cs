using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.MarketData;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Jobs;

/// <summary>
/// Daily 05:00 UTC: stores USD/ZAR plus every instrument currency against both
/// ZAR and USD for today (the providers walk back over weekends).
/// </summary>
[AutomaticRetry(Attempts = 3)]
public sealed class FxSyncJob(EdgewiseDbContext db, MarketDataService market, ILogger<FxSyncJob> logger)
{
    private static readonly string[] AnchorCurrencies = ["ZAR", "USD"];

    public async Task RunAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currencies = await db.Instruments
            .Where(i => i.Currency != "")
            .Select(i => i.Currency.ToUpper())
            .Distinct()
            .ToListAsync(ct);
        currencies.AddRange(AnchorCurrencies);

        foreach (var currency in currencies.Distinct(StringComparer.Ordinal))
        {
            foreach (var anchor in AnchorCurrencies)
            {
                if (currency == anchor)
                {
                    continue;
                }

                try
                {
                    await market.GetFxRateAsync(currency, anchor, today, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "FX sync failed for {Base}/{Quote}.", currency, anchor);
                }
            }
        }
    }
}
