using Edgewise.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Jobs.Portfolio;

/// <summary>
/// Daily (22:00 UTC) portfolio snapshot writer. For every user it values each bucket
/// (best-effort quotes, cost fallback) and upserts one Snapshot row per bucket plus a
/// portfolio-total row (BucketId null), keyed by (user, bucket, date) — re-runs update
/// in place. NetFlowMinor records the day's external cash flows.
/// </summary>
public sealed class SnapshotJob(
    EdgewiseDbContext db,
    PortfolioValuationService valuation,
    ILogger<SnapshotJob> logger)
{
    /// <summary>Hangfire entry point: snapshots every user for today (UTC).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        var userIds = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Select(u => u.Id)
            .ToListAsync(ct);

        var written = 0;
        foreach (var userId in userIds)
        {
            try
            {
                written += await RunForUserAsync(userId, date, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Snapshot failed for user {UserId}.", userId);
            }
        }

        logger.LogInformation(
            "Snapshot run for {Date} wrote {Rows} rows across {Users} users.", date, written, userIds.Count);
    }

    /// <summary>Snapshots a single user for a date. Idempotent upsert; returns rows written.</summary>
    public async Task<int> RunForUserAsync(Guid userId, DateOnly date, CancellationToken ct)
    {
        var baseCurrency = await valuation.GetBaseCurrencyAsync(userId, ct);
        var bucketEquities = await valuation.ValueBucketsAsync(userId, ct);
        var dailyFlowsByBucket = new Dictionary<Guid, Dictionary<DateOnly, long>>();
        foreach (var equity in bucketEquities)
        {
            dailyFlowsByBucket[equity.Bucket.Id] =
                await valuation.GetDailyNetFlowsAsync(userId, equity.Bucket.Id, ct);
        }

        var existing = await db.Snapshots.IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.Date == date)
            .ToListAsync(ct);

        var written = 0;
        long totalEquity = 0;
        long totalNetFlow = 0;
        foreach (var equity in bucketEquities)
        {
            var netFlow = dailyFlowsByBucket[equity.Bucket.Id].GetValueOrDefault(date);
            totalEquity += equity.ValueMinor;
            totalNetFlow += netFlow;
            Upsert(existing, userId, equity.Bucket.Id, date, equity.ValueMinor, netFlow, baseCurrency);
            written++;
        }

        // Portfolio-total row.
        Upsert(existing, userId, bucketId: null, date, totalEquity, totalNetFlow, baseCurrency);
        written++;

        await db.SaveChangesAsync(ct);
        return written;
    }

    private void Upsert(
        List<Domain.Entities.Snapshot> existing,
        Guid userId,
        Guid? bucketId,
        DateOnly date,
        long equityMinor,
        long netFlowMinor,
        string currency)
    {
        var row = existing.FirstOrDefault(s => s.BucketId == bucketId);
        if (row is null)
        {
            db.Snapshots.Add(new Domain.Entities.Snapshot
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BucketId = bucketId,
                Date = date,
                EquityMinor = equityMinor,
                Currency = currency,
                NetFlowMinor = netFlowMinor,
            });
        }
        else
        {
            row.EquityMinor = equityMinor;
            row.NetFlowMinor = netFlowMinor;
            row.Currency = currency;
        }
    }
}

/// <summary>Registers the portfolio valuation services and the snapshot job.</summary>
public sealed class PortfolioJobsModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<PortfolioMarketData>();
        services.AddScoped<PortfolioValuationService>();
        services.AddScoped<SnapshotJob>();
    }
}

/// <summary>Schedules the daily snapshot at 22:00 UTC.</summary>
public sealed class PortfolioJobRegistrar : IRecurringJobRegistrar
{
    public void Register(IRecurringJobManager jobs) =>
        jobs.AddOrUpdate<SnapshotJob>(
            "portfolio-daily-snapshot",
            job => job.RunAsync(CancellationToken.None),
            "0 22 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
}
