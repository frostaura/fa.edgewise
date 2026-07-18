using Edgewise.Api.Services.Journal;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>Lightweight decision log: Enter / Exit / Skip with a reason.</summary>
public sealed class DecisionsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var decisions = app.MapGroup("/api/decisions").RequireAuthorization();

        decisions.MapGet("/", async (
            DecisionKind? kind, int? limit, EdgewiseDbContext db, CancellationToken ct) =>
        {
            var query = db.DecisionLogs.AsNoTracking();
            if (kind is not null)
            {
                query = query.Where(d => d.Kind == kind);
            }

            var items = await query
                .OrderByDescending(d => d.At)
                .Take(Math.Clamp(limit ?? 50, 1, 200))
                .Select(d => new DecisionDto(d.Id, d.Kind, d.InstrumentId, d.PlanId, d.Reason, d.At))
                .ToListAsync(ct);
            return Results.Ok(items);
        });

        decisions.MapPost("/", async (
            CreateDecisionRequest request,
            EdgewiseDbContext db,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                throw ApiException.BadRequest("reason_required", "A reason is required.");
            }

            var decision = new DecisionLog
            {
                Id = Guid.NewGuid(),
                UserId = JournalCommon.RequireUserId(currentUser),
                Kind = request.Kind,
                InstrumentId = request.InstrumentId,
                PlanId = request.PlanId,
                Reason = request.Reason.Trim(),
                At = DateTime.UtcNow,
            };
            db.DecisionLogs.Add(decision);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/decisions/{decision.Id}",
                new DecisionDto(decision.Id, decision.Kind, decision.InstrumentId, decision.PlanId,
                    decision.Reason, decision.At));
        });
    }
}
