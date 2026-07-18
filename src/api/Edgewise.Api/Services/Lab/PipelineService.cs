using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Lab;

public sealed record PipelineGateProgress(
    int HonestDoneBacktests,
    int PaperTrades,
    int PaperTradesRequired,
    decimal? AvgAdherence,
    decimal AdherenceRequired,
    int LiveTrades,
    int LiveTradesRequired,
    decimal? LiveExpectancyR);

public sealed record PipelineHistoryEntry(
    StrategyState FromState,
    StrategyState ToState,
    DateTime At,
    JsonElement? Evidence);

public sealed record PipelineDto(
    Guid StrategyId,
    StrategyState State,
    int CurrentVersion,
    PipelineGateProgress Gates,
    IReadOnlyList<PipelineHistoryEntry> History);

/// <summary>
/// The Draft -> Backtested -> Paper -> TinyLive -> Live promotion pipeline. Forward moves
/// go one stage at a time and must pass their gate (or be forced with a logged reason);
/// moving backward is always allowed. Every transition writes a PipelineStateChange with
/// a computed evidence snapshot.
/// </summary>
public sealed class PipelineService(EdgewiseDbContext db)
{
    public const int PaperTradesRequired = 25;
    public const decimal AdherenceRequired = 90m;
    public const int LiveTradesRequired = 30;

    public async Task<PipelineDto> GetPipelineAsync(Strategy strategy, CancellationToken ct)
    {
        var gates = await ComputeGatesAsync(strategy, ct);
        var history = await db.PipelineStateChanges
            .Where(p => p.StrategyId == strategy.Id)
            .OrderByDescending(p => p.At)
            .ToListAsync(ct);

        return new PipelineDto(
            strategy.Id,
            strategy.State,
            strategy.CurrentVersion,
            gates,
            history
                .Select(h => new PipelineHistoryEntry(h.FromState, h.ToState, h.At, LabJson.ParseOrNull(h.GatesEvidenceJson)))
                .ToList());
    }

    public async Task<PipelineDto> TransitionAsync(
        Strategy strategy, StrategyState toState, bool force, string? reason, CancellationToken ct)
    {
        var fromState = strategy.State;
        if (toState == fromState)
        {
            throw ApiException.BadRequest("invalid_transition", $"Strategy is already in state '{fromState}'.");
        }

        var movingBack = toState < fromState;
        if (!movingBack && toState != fromState + 1)
        {
            throw ApiException.BadRequest(
                "invalid_transition",
                $"Cannot move from '{fromState}' to '{toState}'. Forward transitions go one stage at a time.");
        }

        if (force && string.IsNullOrWhiteSpace(reason))
        {
            throw ApiException.BadRequest("reason_required", "Forcing a transition requires a reason.");
        }

        var gates = await ComputeGatesAsync(strategy, ct);
        string? failure = movingBack
            ? null
            : toState switch
            {
                StrategyState.Backtested when gates.HonestDoneBacktests == 0 =>
                    "Requires at least one completed backtest of the current version with out-of-sample evidence (not exploratory).",
                StrategyState.TinyLive when gates.PaperTrades < PaperTradesRequired =>
                    $"Requires at least {PaperTradesRequired} paper trades tagged to this strategy ({gates.PaperTrades} so far).",
                StrategyState.TinyLive when (gates.AvgAdherence ?? 0m) < AdherenceRequired =>
                    $"Requires average plan adherence of at least {AdherenceRequired:0} across paper trades (currently {gates.AvgAdherence?.ToString("0.#") ?? "unscored"}).",
                StrategyState.Live when gates.LiveTrades < LiveTradesRequired =>
                    $"Requires at least {LiveTradesRequired} closed live trades tagged to this strategy ({gates.LiveTrades} so far).",
                StrategyState.Live when (gates.LiveExpectancyR ?? 0m) <= 0m =>
                    "Requires positive live expectancy (average realised R must be greater than 0).",
                _ => null,
            };

        if (failure is not null && !force)
        {
            throw new ApiException(422, "gate_not_met", failure);
        }

        var evidence = new Dictionary<string, object?>
        {
            ["gates"] = gates,
            ["forced"] = force,
        };
        if (force)
        {
            evidence["reason"] = reason;
            if (failure is not null)
            {
                evidence["gateFailure"] = failure;
            }
        }

        strategy.State = toState;
        db.PipelineStateChanges.Add(new PipelineStateChange
        {
            Id = Guid.NewGuid(),
            StrategyId = strategy.Id,
            FromState = fromState,
            ToState = toState,
            GatesEvidenceJson = LabJson.Serialize(evidence),
            At = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        return await GetPipelineAsync(strategy, ct);
    }

    private async Task<PipelineGateProgress> ComputeGatesAsync(Strategy strategy, CancellationToken ct)
    {
        // Draft -> Backtested: >= 1 Done backtest on the CURRENT version that is not exploratory.
        var currentVersionId = await db.StrategyVersions
            .Where(v => v.StrategyId == strategy.Id && v.Version == strategy.CurrentVersion)
            .Select(v => (Guid?)v.Id)
            .SingleOrDefaultAsync(ct);

        var honestDone = 0;
        if (currentVersionId is Guid versionId)
        {
            var honestyJsons = await db.Backtests
                .Where(b => b.StrategyVersionId == versionId && b.Status == BacktestStatus.Done && b.HonestyJson != null)
                .Select(b => b.HonestyJson!)
                .ToListAsync(ct);
            honestDone = honestyJsons.Count(json =>
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("isExploratory", out var flag)
                    && flag.ValueKind == JsonValueKind.False;
            });
        }

        // Paper -> TinyLive: >= 25 paper trades tagged with this strategy, avg adherence >= 90.
        var paperTrades = await db.Trades
            .CountAsync(t => t.StrategyId == strategy.Id && t.IsPaper, ct);
        var adherenceScores = await db.AdherenceResults
            .Where(a => a.Trade.StrategyId == strategy.Id && a.Trade.IsPaper)
            .GroupBy(a => a.TradeId)
            .Select(g => g.OrderByDescending(a => a.ComputedAt).First().Score)
            .ToListAsync(ct);
        decimal? avgAdherence = adherenceScores.Count > 0
            ? Math.Round((decimal)adherenceScores.Average(), 1)
            : null;

        // TinyLive -> Live: >= 30 closed live trades with positive expectancy (avg realised R).
        var liveRs = await db.Trades
            .Where(t => t.StrategyId == strategy.Id && !t.IsPaper && t.Status == TradeStatus.Closed)
            .Select(t => t.RRealised)
            .ToListAsync(ct);
        var liveWithR = liveRs.Where(r => r.HasValue).Select(r => r!.Value).ToList();
        decimal? liveExpectancy = liveWithR.Count > 0 ? Math.Round(liveWithR.Average(), 3) : null;

        return new PipelineGateProgress(
            honestDone,
            paperTrades,
            PaperTradesRequired,
            avgAdherence,
            AdherenceRequired,
            liveRs.Count,
            LiveTradesRequired,
            liveExpectancy);
    }
}
