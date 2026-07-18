using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Edgewise.Infrastructure.Jobs.Coach;

/// <summary>
/// Hangfire job: nightly (01:00 UTC) coach batch. For every user who has not opted out of the
/// LLM features it generates post-mortems for freshly closed trades, refreshes bias cards, and
/// (on Sundays in the user's timezone) drafts the weekly review pack. Per-user work is
/// delegated to <see cref="ICoachNightlyWorker"/>; budget/opt-out gating happens inside the
/// LLM gateway, so users over budget still receive deterministic insights.
/// </summary>
public sealed class NightlyCoachBatch(IServiceProvider services, ILogger<NightlyCoachBatch> logger)
{
    private readonly IServiceProvider _services = services;
    private readonly ILogger<NightlyCoachBatch> _logger = logger;

    public async Task RunAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var worker = scope.ServiceProvider.GetService<ICoachNightlyWorker>();
        if (worker is null)
        {
            _logger.LogWarning("NightlyCoachBatch: no ICoachNightlyWorker registered; skipping run.");
            return;
        }

        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<EdgewiseDbContext>>();
        List<Guid> userIds;
        await using (var db = new EdgewiseDbContext(dbOptions, new FixedCurrentUser(null)))
        {
            userIds = await db.Users
                .IgnoreQueryFilters()
                .Where(u => !u.LlmOptOut)
                .Select(u => u.Id)
                .ToListAsync(ct);
        }

        var now = DateTime.UtcNow;
        foreach (var userId in userIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await worker.RunForUserAsync(userId, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One user's failure must not sink the batch.
                _logger.LogError(ex, "NightlyCoachBatch failed for user {UserId}", userId);
            }
        }
    }
}
