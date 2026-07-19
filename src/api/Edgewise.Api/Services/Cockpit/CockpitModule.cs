using Edgewise.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Edgewise.Api.Services.Cockpit;

/// <summary>Registers cockpit feature services (discovered by Program.cs).</summary>
public sealed class CockpitModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<CockpitService>();
    }
}
