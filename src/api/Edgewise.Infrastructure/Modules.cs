using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Edgewise.Infrastructure;

/// <summary>
/// Discovered at startup and invoked before the container is built. Implementations
/// must have a parameterless constructor; use this to register feature services,
/// HttpClients, and Hangfire job classes without editing Program.cs.
/// </summary>
public interface IServiceModule
{
    void Configure(IServiceCollection services, IConfiguration config);
}

/// <summary>
/// Discovered at startup (when Hangfire is enabled) and invoked with the recurring
/// job manager. Instances are created through DI (ActivatorUtilities), so constructor
/// injection is available.
/// </summary>
public interface IRecurringJobRegistrar
{
    void Register(IRecurringJobManager jobs);
}
