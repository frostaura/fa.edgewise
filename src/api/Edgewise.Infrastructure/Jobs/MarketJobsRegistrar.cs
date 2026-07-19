using Hangfire;

namespace Edgewise.Infrastructure.Jobs;

/// <summary>Cron schedules for the market-data jobs (all times UTC).</summary>
public sealed class MarketJobsRegistrar : IRecurringJobRegistrar
{
    public void Register(IRecurringJobManager jobs)
    {
        var utc = new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc };

        jobs.AddOrUpdate<PriceSyncJob>(
            "market-quote-sync", job => job.SyncQuotesAsync(CancellationToken.None), "*/15 * * * *", utc);
        jobs.AddOrUpdate<PriceSyncJob>(
            "market-bar-sync", job => job.SyncBarsAsync(CancellationToken.None), "5 * * * *", utc);
        jobs.AddOrUpdate<FxSyncJob>(
            "market-fx-sync", job => job.RunAsync(CancellationToken.None), "0 5 * * *", utc);
        jobs.AddOrUpdate<SentimentJob>(
            "market-sentiment-sync", job => job.RunAsync(CancellationToken.None), "15 5 * * *", utc);
        jobs.AddOrUpdate<NewsPollJob>(
            "market-news-poll", job => job.RunAsync(CancellationToken.None), "*/10 * * * *", utc);
        jobs.AddOrUpdate<CalendarSyncJob>(
            "market-calendar-sync", job => job.RunAsync(CancellationToken.None), "30 5,17 * * *", utc);
    }
}
