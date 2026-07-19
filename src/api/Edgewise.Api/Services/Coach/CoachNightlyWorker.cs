using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Jobs.Coach;
using Edgewise.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Coach;

/// <summary>
/// Per-user nightly coach work. Builds a user-scoped DbContext (FixedCurrentUser) so the same
/// orchestrator code path used by HTTP requests runs safely inside the Hangfire batch.
/// </summary>
public sealed class CoachNightlyWorker(
    DbContextOptions<EdgewiseDbContext> dbOptions,
    ILlmProvider provider,
    LlmOptions options) : ICoachNightlyWorker
{
    public async Task RunForUserAsync(Guid userId, DateTime utcNow, CancellationToken ct)
    {
        await using var db = new EdgewiseDbContext(dbOptions, new FixedCurrentUser(userId));
        var gateway = new LlmGateway(provider, db, options);
        var orchestrator = new CoachOrchestrator(db, gateway);

        // 1. Post-mortems for trades closed in the last 24h lacking a non-superseded PostMortem.
        var since = utcNow.AddHours(-24);
        var closedTradeIds = await db.Trades
            .Where(t => t.Status == TradeStatus.Closed && t.ClosedAt >= since)
            .Select(t => t.Id)
            .ToListAsync(ct);
        var covered = await db.Insights
            .Where(i => i.Type == InsightType.PostMortem
                && i.SupersededById == null
                && i.TradeId != null
                && closedTradeIds.Contains(i.TradeId.Value))
            .Select(i => i.TradeId!.Value)
            .ToListAsync(ct);
        foreach (var tradeId in closedTradeIds.Except(covered))
        {
            await orchestrator.GeneratePostMortemAsync(tradeId, ct);
        }

        // 2. Refresh bias cards over the last 90 days.
        await orchestrator.RefreshBiasCardsAsync(ct);

        // 3. Sundays (user timezone): draft the weekly pack for the week ending today.
        var timezone = ResolveTimeZone(await db.Users.Select(u => u.Timezone).SingleAsync(ct));
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timezone);
        if (localNow.DayOfWeek == DayOfWeek.Sunday)
        {
            var weekStart = DateOnly.FromDateTime(localNow.Date.AddDays(-6)); // Monday of the ending week
            await orchestrator.GenerateWeeklyPackAsync(weekStart, ct);
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
        {
            return tz;
        }

        return TimeZoneInfo.Utc;
    }
}
