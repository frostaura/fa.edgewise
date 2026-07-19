using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Edgewise.Infrastructure.Jobs.Alerts;

/// <summary>DI registrations for the alert evaluation pipeline.</summary>
public sealed class AlertsServiceModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<AlertEvaluationJob>();
        services.AddSingleton<WebPushSender>();
        services.AddSingleton<EmailSender>();
    }
}

/// <summary>Schedules alert evaluation every minute.</summary>
public sealed class AlertsJobRegistrar : IRecurringJobRegistrar
{
    public void Register(IRecurringJobManager jobs) =>
        jobs.AddOrUpdate<AlertEvaluationJob>(
            "alerts-evaluate",
            job => job.RunAsync(CancellationToken.None),
            Cron.Minutely);
}
