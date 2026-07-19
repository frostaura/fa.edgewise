using Edgewise.Infrastructure;
using Edgewise.Infrastructure.Jobs.Coach;

namespace Edgewise.Api.Services.Coach;

/// <summary>Registers the coach vertical's services (discovered by Program at startup).</summary>
public sealed class CoachServiceModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<CoachOrchestrator>();
        services.AddScoped<CoachChatService>();
        services.AddScoped<ICoachNightlyWorker, CoachNightlyWorker>();
    }
}
