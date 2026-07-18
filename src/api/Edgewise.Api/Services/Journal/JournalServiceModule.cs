using Edgewise.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Edgewise.Api.Services.Journal;

/// <summary>Registers the journal vertical's application services (auto-discovered).</summary>
public sealed class JournalServiceModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<BucketEquityService>();
        services.AddScoped<PlanService>();
        services.AddScoped<AdherencePipeline>();
        services.AddScoped<TradeService>();
        services.AddScoped<FillInboxService>();
        services.AddScoped<CsvImportService>();
        services.AddScoped<JournalAnalyticsService>();
    }
}
