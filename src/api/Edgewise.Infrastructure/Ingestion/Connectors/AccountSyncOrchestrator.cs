using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Ingestion.Connectors;

/// <summary>
/// Runs one incremental sync for a single account: dispatches to the right
/// connector, updates Account.LastSyncAt / LastSyncStatsJson / Status and the
/// per-provider ProviderHealth row. Failures set Status=Error with a
/// plain-language message in stats (INT-005) — secrets never appear in stats,
/// logs or health rows. Works both inside an authenticated request scope and in
/// Hangfire jobs (all queries bypass the per-user filters; UserId is set explicitly).
/// </summary>
public sealed class AccountSyncOrchestrator(
    EdgewiseDbContext db,
    PolymarketSyncService polymarket,
    BinanceSyncService binance,
    CoinbaseSyncService coinbase,
    ILogger<AccountSyncOrchestrator> logger)
{
    public static bool IsSyncableVenue(Venue venue) =>
        venue is Venue.Polymarket or Venue.Binance or Venue.Coinbase;

    public async Task SyncAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await db.Accounts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null || account.Status == AccountStatus.Revoked || !IsSyncableVenue(account.Venue))
        {
            return;
        }

        var stats = AccountSyncStats.Parse(account.LastSyncStatsJson);
        stats.Syncing = true;
        stats.Error = null;
        account.LastSyncStatsJson = stats.ToJson();
        await db.SaveChangesAsync(ct);

        var provider = ProviderKey(account.Venue);
        try
        {
            var imported = account.Venue switch
            {
                Venue.Polymarket => await polymarket.SyncAsync(db, account, stats, ct),
                Venue.Binance => await binance.SyncAsync(db, account, stats, ct),
                Venue.Coinbase => await coinbase.SyncAsync(db, account, stats, ct),
                _ => 0,
            };

            stats.LastRunFills = imported;
            stats.TotalFills = await db.Fills.IgnoreQueryFilters()
                .LongCountAsync(f => f.AccountId == account.Id, ct);
            stats.LastRunStatus = "ok";
            account.Status = AccountStatus.Connected;
            await UpsertProviderHealthAsync(provider, success: true, note: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex is IntegrationException integration
                ? integration.Message
                : "Sync failed unexpectedly — we will retry on the next scheduled run.";
            stats.LastRunStatus = "error";
            stats.Error = message;
            account.Status = AccountStatus.Error;
            await UpsertProviderHealthAsync(provider, success: false, note: message, ct);

            // Log the type only — exception messages from connectors are secret-free
            // by design, but never log payloads or credentials here.
            logger.LogWarning(
                "Sync failed for account {AccountId} ({Venue}): {Code}",
                account.Id, account.Venue, (ex as IntegrationException)?.Code ?? ex.GetType().Name);
        }
        finally
        {
            stats.Syncing = false;
            stats.CurrentSymbol = null;
            stats.LastRunAt = DateTime.UtcNow;
            account.LastSyncAt = DateTime.UtcNow;
            account.LastSyncStatsJson = stats.ToJson();
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    public static string ProviderKey(Venue venue) => $"{venue.ToString().ToLowerInvariant()}-user";

    private async Task UpsertProviderHealthAsync(string provider, bool success, string? note, CancellationToken ct)
    {
        var health = await db.ProviderHealths.FirstOrDefaultAsync(p => p.Provider == provider, ct);
        if (health is null)
        {
            health = new ProviderHealth { Provider = provider };
            db.ProviderHealths.Add(health);
        }

        if (success)
        {
            health.LastSuccessAt = DateTime.UtcNow;
            health.ErrorNote = null;
        }
        else
        {
            health.LastErrorAt = DateTime.UtcNow;
            health.ErrorNote = note;
        }
    }
}
