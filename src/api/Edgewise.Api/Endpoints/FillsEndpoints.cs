using Edgewise.Api.Services.Journal;

namespace Edgewise.Api.Endpoints;

/// <summary>Manual fills and the match-or-confess inbox.</summary>
public sealed class FillsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var fills = app.MapGroup("/api/fills").RequireAuthorization();

        fills.MapPost("/", async (CreateFillRequest request, FillInboxService service, CancellationToken ct) =>
        {
            var fill = await service.CreateManualAsync(request, ct);
            return Results.Created($"/api/fills/{fill.Id}", fill);
        });

        fills.MapGet("/inbox", (FillInboxService service, CancellationToken ct) =>
            service.GetInboxAsync(ct));

        fills.MapGet("/inbox/count", async (FillInboxService service, CancellationToken ct) =>
            Results.Ok(new { count = await service.GetInboxCountAsync(ct) }));

        fills.MapPost("/{id:guid}/match", (Guid id, MatchFillRequest request, FillInboxService service, CancellationToken ct) =>
            service.MatchAsync(id, request, ct));

        fills.MapPost("/{id:guid}/confess", (Guid id, FillInboxService service, CancellationToken ct) =>
            service.ConfessAsync(id, ct));

        fills.MapDelete("/{id:guid}", async (Guid id, FillInboxService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }
}
