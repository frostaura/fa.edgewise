using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Ingestion.Connectors;

/// <summary>
/// Hourly recurring job: incremental sync of every Connected (or errored —
/// errors are retried) exchange account across all users. Per-account failures
/// are isolated by the orchestrator and never abort the batch.
/// </summary>
public sealed class ExchangeSyncJob(EdgewiseDbContext db, AccountSyncOrchestrator orchestrator)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var accountIds = await db.Accounts.IgnoreQueryFilters()
            .Where(a => a.Status != AccountStatus.Revoked
                && (a.Venue == Venue.Polymarket || a.Venue == Venue.Binance || a.Venue == Venue.Coinbase))
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var accountId in accountIds)
        {
            ct.ThrowIfCancellationRequested();
            await orchestrator.SyncAccountAsync(accountId, ct); // per-account try/catch inside
        }
    }

    /// <summary>Single-account entry point used by POST /api/accounts/{id}/sync when Hangfire is enabled.</summary>
    public Task SyncAccountAsync(Guid accountId) => orchestrator.SyncAccountAsync(accountId);
}

/// <summary>
/// Hourly recurring job: resolves open BrierForecasts via the Polymarket gamma
/// markets API. Forecast ↔ market matching uses the conditionId + outcome the
/// sync embedded in RulesUrl. BrierScore = (pUser − outcome)².
/// </summary>
public sealed class BrierResolutionJob(
    EdgewiseDbContext db, PolymarketClient client, ILogger<BrierResolutionJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var open = await db.BrierForecasts.IgnoreQueryFilters()
            .Where(f => f.Outcome == null && f.RulesUrl != null
                && f.RulesUrl.StartsWith("https://polymarket.com/market?cid="))
            .ToListAsync(ct);
        if (open.Count == 0)
        {
            return;
        }

        var byCondition = open
            .Select(f => (Forecast: f, Parsed: ParseMarker(f.RulesUrl!)))
            .Where(x => x.Parsed is not null)
            .GroupBy(x => x.Parsed!.Value.ConditionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var chunk in byCondition.Keys.Chunk(20))
        {
            IReadOnlyList<PolymarketMarket> markets;
            try
            {
                markets = await client.GetMarketsByConditionIdsAsync(chunk, ct);
            }
            catch (IntegrationException ex)
            {
                logger.LogWarning("Brier resolution fetch failed: {Code}", ex.Code);
                continue;
            }

            foreach (var market in markets)
            {
                if (!byCondition.TryGetValue(market.ConditionId, out var entries))
                {
                    continue;
                }

                var endDate = ParseEndDate(market.EndDate);
                var winner = market.Closed ? WinningOutcome(market) : null;

                foreach (var (forecast, parsed) in entries)
                {
                    if (endDate is not null)
                    {
                        forecast.ResolutionDate = endDate.Value;
                    }

                    if (winner is null)
                    {
                        continue;
                    }

                    var won = string.Equals(winner, parsed!.Value.Outcome, StringComparison.OrdinalIgnoreCase);
                    forecast.Outcome = won;
                    forecast.BrierScore = PolymarketSyncService.BrierScore(forecast.PUser, won);
                    forecast.ResolvedAt = DateTime.UtcNow;
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    internal static (string ConditionId, string Outcome)? ParseMarker(string rulesUrl)
    {
        var queryStart = rulesUrl.IndexOf('?');
        if (queryStart < 0)
        {
            return null;
        }

        string? cid = null;
        string? outcome = null;
        foreach (var pair in rulesUrl[(queryStart + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = pair[..eq];
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (key == "cid")
            {
                cid = value;
            }
            else if (key == "outcome")
            {
                outcome = value;
            }
        }

        return cid is null ? null : (cid, outcome ?? "Yes");
    }

    private static string? WinningOutcome(PolymarketMarket market)
    {
        var names = market.OutcomeNames();
        var prices = market.Prices();
        if (names.Count == 0 || prices.Count != names.Count)
        {
            return null;
        }

        var winnerIndex = 0;
        for (var i = 1; i < prices.Count; i++)
        {
            if (prices[i] > prices[winnerIndex])
            {
                winnerIndex = i;
            }
        }

        // Ambiguous (e.g. all 0.5) → leave unresolved.
        return prices[winnerIndex] >= 0.9m ? names[winnerIndex] : null;
    }

    private static DateTime? ParseEndDate(string? endDate) =>
        DateTimeOffset.TryParse(endDate, out var parsed) ? parsed.UtcDateTime : null;
}

/// <summary>DI registrations for the integrations vertical (discovered by Program.cs).</summary>
public sealed class IntegrationsServiceModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        // TryAdd so integration tests can pre-register a fake factory.
        services.TryAddSingleton<IIntegrationHttpClientFactory, DefaultIntegrationHttpClientFactory>();
        services.AddSingleton<BinanceRateBudget>();

        services.AddScoped<PolymarketClient>();
        services.AddScoped<BinanceClient>();
        services.AddScoped<CoinbaseClient>();

        services.AddScoped<PolymarketSyncService>();
        services.AddScoped<BinanceSyncService>();
        services.AddScoped<CoinbaseSyncService>();
        services.AddScoped<AccountSyncOrchestrator>();

        services.AddScoped<ExchangeSyncJob>();
        services.AddScoped<BrierResolutionJob>();
    }
}

/// <summary>Recurring job schedule (only runs when Hangfire is enabled).</summary>
public sealed class IntegrationsJobRegistrar : IRecurringJobRegistrar
{
    public void Register(IRecurringJobManager jobs)
    {
        jobs.AddOrUpdate<ExchangeSyncJob>(
            "integrations-exchange-sync", job => job.RunAsync(CancellationToken.None), Cron.Hourly());
        jobs.AddOrUpdate<BrierResolutionJob>(
            "integrations-brier-resolution", job => job.RunAsync(CancellationToken.None), Cron.Hourly(30));
    }
}
