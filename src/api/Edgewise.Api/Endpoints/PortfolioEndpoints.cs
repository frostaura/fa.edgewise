using Edgewise.Api.Services.Portfolio;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Jobs.Portfolio;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// /api/portfolio — summary, ladder state machine, ratchet, performance (TWR/XIRR),
/// allocation rings, net-worth series and the manual snapshot trigger.
/// </summary>
public sealed class PortfolioEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portfolio").RequireAuthorization();
        group.MapGet("/summary", Summary);
        group.MapGet("/ladder-state", LadderState);
        group.MapGet("/ratchet/suggestion", RatchetSuggestion);
        group.MapPost("/ratchet/accept", RatchetAccept);
        group.MapGet("/performance", Performance);
        group.MapGet("/allocation", Allocation);
        group.MapGet("/networth", Networth);
        group.MapPost("/snapshot/run", RunSnapshot);
    }

    private static async Task<IResult> Summary(PortfolioService portfolio, CancellationToken ct) =>
        Results.Ok(await portfolio.GetSummaryAsync(ct));

    private static async Task<IResult> LadderState(PortfolioService portfolio, CancellationToken ct) =>
        Results.Ok(await portfolio.GetLadderStateAsync(ct));

    private static async Task<IResult> RatchetSuggestion(PortfolioService portfolio, CancellationToken ct)
    {
        var suggestion = await portfolio.GetRatchetSuggestionAsync(ct);
        return suggestion is null ? Results.NoContent() : Results.Ok(suggestion);
    }

    private static async Task<IResult> RatchetAccept(
        AcceptRatchetRequest request, PortfolioService portfolio, CancellationToken ct) =>
        Results.Ok(await portfolio.AcceptRatchetAsync(request.AmountMinor, ct));

    private static async Task<IResult> Performance(
        Guid? bucketId, string? basis, DateOnly? from, DateOnly? to,
        PortfolioService portfolio, CancellationToken ct) =>
        Results.Ok(await portfolio.GetPerformanceAsync(bucketId, basis ?? "twr", from, to, ct));

    private static async Task<IResult> Allocation(PortfolioService portfolio, CancellationToken ct) =>
        Results.Ok(await portfolio.GetAllocationAsync(ct));

    private static async Task<IResult> Networth(
        DateOnly? from, DateOnly? to, PortfolioService portfolio, CancellationToken ct) =>
        Results.Ok(await portfolio.GetNetworthAsync(from, to, ct));

    /// <summary>Runs today's snapshot for the current user only (the daily job covers everyone).</summary>
    private static async Task<IResult> RunSnapshot(
        SnapshotJob job, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await job.RunForUserAsync(userId, date, ct);
        return Results.Ok(new SnapshotRunResultDto(date, rows));
    }
}
