using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Edgewise.Infrastructure.Jobs.Coach;

/// <summary>Registers the nightly coach batch job class.</summary>
public sealed class CoachJobsModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<NightlyCoachBatch>();
    }
}

/// <summary>Schedules the nightly coach batch at 01:00 UTC.</summary>
public sealed class NightlyCoachBatchRegistrar : IRecurringJobRegistrar
{
    public void Register(IRecurringJobManager jobs)
    {
        jobs.AddOrUpdate<NightlyCoachBatch>(
            "nightly-coach-batch",
            job => job.RunAsync(CancellationToken.None),
            "0 1 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
