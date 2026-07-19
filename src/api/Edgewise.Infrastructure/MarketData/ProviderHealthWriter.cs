using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.MarketData;

/// <summary>
/// Upserts ProviderHealth rows via raw SQL (so health writes never interfere with
/// the caller's change tracker). Deliberately swallows every failure — a broken
/// health write must never break a data path.
/// </summary>
public sealed class ProviderHealthWriter(EdgewiseDbContext db, ILogger<ProviderHealthWriter> logger)
{
    public async Task ReportSuccessAsync(string provider, CancellationToken ct = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "ProviderHealths" ("Provider", "LastSuccessAt", "LastErrorAt", "ErrorNote")
                VALUES ({provider}, {now}, NULL, NULL)
                ON CONFLICT ("Provider") DO UPDATE SET "LastSuccessAt" = {now}
                """,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ProviderHealth success write failed for {Provider}.", provider);
        }
    }

    public async Task ReportErrorAsync(string provider, string note, CancellationToken ct = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var trimmed = note.Length > 500 ? note[..500] : note;
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "ProviderHealths" ("Provider", "LastSuccessAt", "LastErrorAt", "ErrorNote")
                VALUES ({provider}, NULL, {now}, {trimmed})
                ON CONFLICT ("Provider") DO UPDATE SET "LastErrorAt" = {now}, "ErrorNote" = {trimmed}
                """,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ProviderHealth error write failed for {Provider}.", provider);
        }
    }
}
