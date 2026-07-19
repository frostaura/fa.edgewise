using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Journal;

/// <summary>
/// Best-effort bucket equity in minor units: the latest Snapshot row for the
/// bucket wins; otherwise falls back to the sum of lot costs plus net cash
/// flows for the bucket. Documented approximation: equity is "as of now",
/// not reconstructed as of a historical entry timestamp.
/// </summary>
public sealed class BucketEquityService(EdgewiseDbContext db)
{
    public async Task<long> GetEquityMinorAsync(Guid bucketId, CancellationToken ct)
    {
        var snapshot = await db.Snapshots
            .Where(s => s.BucketId == bucketId)
            .OrderByDescending(s => s.Date)
            .Select(s => (long?)s.EquityMinor)
            .FirstOrDefaultAsync(ct);
        if (snapshot is not null)
        {
            return snapshot.Value;
        }

        var lotCosts = await db.Lots
            .Where(l => l.Holding.BucketId == bucketId)
            .SumAsync(l => (long?)l.CostMinor, ct) ?? 0L;
        var cashFlows = await db.CashFlows
            .Where(c => c.BucketId == bucketId)
            .SumAsync(c => (long?)c.AmountMinor, ct) ?? 0L;

        return lotCosts + cashFlows;
    }
}
