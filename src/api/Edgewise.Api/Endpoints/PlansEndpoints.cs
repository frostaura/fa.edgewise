using Edgewise.Api.Services.Journal;
using Edgewise.Domain.Entities;

namespace Edgewise.Api.Endpoints;

/// <summary>Trade plans, playbook templates, sizing preview and plan-composer lookups.</summary>
public sealed class PlansEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var plans = app.MapGroup("/api/plans").RequireAuthorization();

        plans.MapGet("/", (string? status, PlanService service, CancellationToken ct) =>
            service.ListAsync(JournalCommon.ParseEnum<TradePlanStatus>(status, "status"), ct));

        plans.MapGet("/size-preview", (
                Guid bucketId, decimal stopPrice, decimal entryPrice, Guid? riskProfileId,
                PlanService service, CancellationToken ct) =>
            service.SizePreviewAsync(bucketId, stopPrice, entryPrice, riskProfileId, ct));

        plans.MapGet("/lookups", (PlanService service, CancellationToken ct) =>
            service.LookupsAsync(ct));

        plans.MapPost("/instruments", async (CreateInstrumentRequest request, PlanService service, CancellationToken ct) =>
            Results.Created("/api/plans/lookups", await service.CreateInstrumentAsync(request, ct)));

        plans.MapGet("/{id:guid}", (Guid id, PlanService service, CancellationToken ct) =>
            service.GetAsync(id, ct));

        plans.MapPost("/", async (CreatePlanRequest request, PlanService service, CancellationToken ct) =>
        {
            var plan = await service.CreateAsync(request, ct);
            return Results.Created($"/api/plans/{plan.Id}", plan);
        });

        plans.MapPatch("/{id:guid}", (Guid id, PatchPlanRequest request, PlanService service, CancellationToken ct) =>
            service.PatchAsync(id, request, ct));

        plans.MapPost("/{id:guid}/cancel", (Guid id, PlanService service, CancellationToken ct) =>
            service.CancelAsync(id, ct));

        plans.MapPost("/{id:guid}/promote", (Guid id, PromotePlanRequest request, PlanService service, CancellationToken ct) =>
            service.PromoteAsync(id, request, ct));

        var templates = app.MapGroup("/api/plan-templates").RequireAuthorization();

        templates.MapGet("/", (PlanService service, CancellationToken ct) =>
            service.ListTemplatesAsync(ct));

        templates.MapPost("/", async (UpsertTemplateRequest request, PlanService service, CancellationToken ct) =>
        {
            var template = await service.CreateTemplateAsync(request, ct);
            return Results.Created($"/api/plan-templates/{template.Id}", template);
        });

        templates.MapPatch("/{id:guid}", (Guid id, UpsertTemplateRequest request, PlanService service, CancellationToken ct) =>
            service.PatchTemplateAsync(id, request, ct));

        templates.MapDelete("/{id:guid}", async (Guid id, PlanService service, CancellationToken ct) =>
        {
            await service.DeleteTemplateAsync(id, ct);
            return Results.NoContent();
        });
    }
}
