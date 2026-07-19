using Edgewise.Api.Services.Cockpit;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// Cockpit: the five reads, self-declared state, and the red-override flow.
///   GET  /api/cockpit/status        → CockpitStatusDto (Journal consumes newPlanUnlocked)
///   POST /api/cockpit/state         { state: calm|tired|tilted|rushed }
///   POST /api/cockpit/override      { reason } → logs OverrideLog(CockpitRed) (+CircuitBreaker when tripped)
///   GET  /api/cockpit/catalysts     ?days=7 → catalyst calendar for Radar
/// </summary>
public sealed class CockpitEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cockpit").RequireAuthorization();

        group.MapGet("/status", async (CockpitService cockpit, CancellationToken ct) =>
            Results.Ok(await cockpit.GetStatusAsync(ct)));

        group.MapPost("/state", async (SetStateRequest request, CockpitService cockpit, CancellationToken ct) =>
        {
            await cockpit.SetStateAsync(request.State ?? string.Empty, ct);
            return Results.Ok(await cockpit.GetStatusAsync(ct));
        });

        group.MapPost("/override", async (OverrideRequest request, CockpitService cockpit, CancellationToken ct) =>
            Results.Ok(await cockpit.OverrideAsync(request.Reason ?? string.Empty, ct)));

        group.MapGet("/catalysts", async (int? days, CockpitService cockpit, CancellationToken ct) =>
            Results.Ok(await cockpit.GetCatalystsAsync(days ?? 7, ct)));
    }

    public sealed record SetStateRequest(string? State);

    public sealed record OverrideRequest(string? Reason);
}
