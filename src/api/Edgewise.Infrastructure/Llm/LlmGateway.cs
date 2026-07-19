using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Infrastructure.Llm;

/// <summary>Snapshot of the LLM gate for one user, surfaced by /api/coach/status.</summary>
public sealed record LlmGateStatus(
    bool Enabled,
    string Provider,
    long BudgetUsedTokens,
    long BudgetTokens,
    bool OptedOut)
{
    public bool OverBudget => BudgetTokens > 0 && BudgetUsedTokens >= BudgetTokens;
    public bool CanCallLlm => Enabled && !OptedOut && !OverBudget;
}

/// <summary>
/// The single choke point for LLM calls: respects User.LlmOptOut, enforces the monthly token
/// budget (sum of this month's TokensIn+TokensOut vs User.MonthlyTokenBudget), logs EVERY call
/// to LlmRequestLog with a cost estimate, and degrades to null (deterministic fallback) on any
/// gate or provider failure. Registered scoped; jobs may construct it manually with a
/// per-user-scoped DbContext.
/// </summary>
public sealed class LlmGateway(ILlmProvider provider, EdgewiseDbContext db, LlmOptions options)
{
    private readonly ILlmProvider _provider = provider;
    private readonly EdgewiseDbContext _db = db;
    private readonly LlmOptions _options = options;

    public LlmOptions Options => _options;
    public bool IsConfigured => _provider.IsConfigured;

    public async Task<LlmGateStatus> GetStatusAsync(CancellationToken ct)
    {
        // Global query filters narrow Users/LlmRequestLogs to the current scope's user.
        var user = await _db.Users.SingleOrDefaultAsync(ct)
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var used = await _db.LlmRequestLogs
            .Where(l => l.At >= monthStart)
            .SumAsync(l => (long)l.TokensIn + l.TokensOut, ct);

        return new LlmGateStatus(
            Enabled: _provider.IsConfigured,
            Provider: _provider.Name,
            BudgetUsedTokens: used,
            BudgetTokens: user.MonthlyTokenBudget,
            OptedOut: user.LlmOptOut);
    }

    /// <summary>
    /// Attempts an LLM completion for <paramref name="userId"/>. Returns null when the provider
    /// is offline, the user opted out, the monthly budget is exhausted, or the call failed —
    /// in every case the caller must produce its deterministic fallback instead.
    /// </summary>
    public async Task<LlmResult?> TryCompleteAsync(Guid userId, string purpose, LlmRequest req, CancellationToken ct)
    {
        if (!_provider.IsConfigured)
        {
            return null;
        }

        var status = await GetStatusAsync(ct);
        if (!status.CanCallLlm)
        {
            return null;
        }

        try
        {
            var result = await _provider.CompleteAsync(req, ct);
            if (result.IsOffline)
            {
                return null;
            }

            await LogAsync(userId, purpose, req.Model, result.TokensIn, result.TokensOut, success: true, ct);
            return result;
        }
        catch (LlmException)
        {
            await LogAsync(userId, purpose, req.Model, 0, 0, success: false, ct);
            return null;
        }
    }

    private async Task LogAsync(
        Guid userId, string purpose, string model, int tokensIn, int tokensOut, bool success, CancellationToken ct)
    {
        _db.LlmRequestLogs.Add(new LlmRequestLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = purpose,
            Model = model,
            TokensIn = tokensIn,
            TokensOut = tokensOut,
            CostMicroUsd = LlmCost.EstimateMicroUsd(_options, model, tokensIn, tokensOut),
            At = DateTime.UtcNow,
            Success = success,
        });
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>ICurrentUser pinned to a specific user id — used by background jobs to build
/// per-user-scoped DbContext instances outside an HTTP request.</summary>
public sealed class FixedCurrentUser(Guid? userId) : ICurrentUser
{
    public Guid? UserId { get; } = userId;
}
