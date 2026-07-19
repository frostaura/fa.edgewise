using Edgewise.Api.Services.Journal;
using Edgewise.Domain.Engines.Adherence;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>The adherence rubric (served as data) and per-trade recompute.</summary>
public sealed class AdherenceEndpoints : IEndpointModule
{
    public sealed record RecomputeRequest(ExitRuleOutcome? ExitRule);

    public void Map(IEndpointRouteBuilder app)
    {
        var adherence = app.MapGroup("/api/adherence").RequireAuthorization();

        adherence.MapGet("/rubric", () => Results.Ok(new
        {
            version = AdherenceEngine.RubricVersion,
            unplannedScoreCap = AdherenceEngine.UnplannedScoreCap,
            rules = AdherenceEngine.Rubric,
        }));

        adherence.MapPost("/trades/{id:guid}/recompute", async (
            Guid id,
            RecomputeRequest? request,
            EdgewiseDbContext db,
            AdherencePipeline pipeline,
            CancellationToken ct) =>
        {
            var trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw ApiException.NotFound("trade_not_found", "Trade not found.");

            var result = await pipeline.ComputeAndPersistAsync(
                trade, request?.ExitRule ?? ExitRuleOutcome.MatchedRule, ct);
            return Results.Ok(JournalCommon.ToDto(result));
        });
    }
}
