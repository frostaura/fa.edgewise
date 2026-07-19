using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Polly;

namespace Edgewise.Api;

/// <summary>
/// Applies EF migrations and shared seed data at startup, retrying up to
/// 10 times at 5-second intervals (Postgres may still be starting). Not
/// registered when EDGEWISE_SKIP_MIGRATE=1.
/// </summary>
public sealed class MigrateAndSeedHostedService(
    IServiceProvider serviceProvider,
    ILogger<MigrateAndSeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var retry = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(
                retryCount: 10,
                sleepDurationProvider: _ => TimeSpan.FromSeconds(5),
                onRetry: (ex, delay, attempt, _) =>
                    logger.LogWarning(ex, "Migration attempt {Attempt} failed; retrying in {Delay}s.", attempt, delay.TotalSeconds));

        await retry.ExecuteAsync(async ct =>
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgewiseDbContext>();
            await db.Database.MigrateAsync(ct);
            await DataSeeder.SeedSharedAsync(db, ct);
            logger.LogInformation("Database migrated and shared seed data applied.");
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
