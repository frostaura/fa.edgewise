using Edgewise.Infrastructure;

namespace Edgewise.Api.Services.Lab;

/// <summary>Registers the Lab vertical's services (discovered at startup).</summary>
public sealed class LabServiceModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<RuleTreeValidator>();
        services.AddScoped<BacktestRunner>();
        services.AddScoped<PipelineService>();
    }
}
