using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// Override log access. Cockpit-red overrides go through POST /api/cockpit/override;
/// this group lists history and lets other verticals log Ladder/SizeOverride rows.
///   GET  /api/overrides?kind=&amp;limit=
///   POST /api/overrides { kind, reason }
/// </summary>
public sealed class OverridesEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/overrides").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
    }

    private static async Task<IResult> ListAsync(
        string? kind, int? limit, EdgewiseDbContext db, CancellationToken ct)
    {
        var query = db.OverrideLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<OverrideKind>(kind, ignoreCase: true, out var parsed))
            {
                throw ApiException.BadRequest(
                    "invalid_kind", "kind must be one of: circuitBreaker, ladder, cockpitRed, sizeOverride.");
            }

            query = query.Where(o => o.Kind == parsed);
        }

        var items = await query
            .OrderByDescending(o => o.At)
            .Take(Math.Clamp(limit ?? 50, 1, 200))
            .ToListAsync(ct);
        return Results.Ok(items.Select(ToDto).ToList());
    }

    private static async Task<IResult> CreateAsync(
        SaveOverrideRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (!Enum.TryParse<OverrideKind>(request.Kind, ignoreCase: true, out var kind))
        {
            throw ApiException.BadRequest(
                "invalid_kind", "kind must be one of: circuitBreaker, ladder, cockpitRed, sizeOverride.");
        }

        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length < 3)
        {
            throw ApiException.BadRequest("reason_required", "An override reason of at least 3 characters is required.");
        }

        var log = new OverrideLog
        {
            Id = Guid.NewGuid(),
            UserId = currentUser.UserId
                ?? throw ApiException.Unauthorized("unauthorized", "Authentication required."),
            Kind = kind,
            Reason = reason,
            At = DateTime.UtcNow,
        };
        db.OverrideLogs.Add(log);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/overrides/{log.Id}", ToDto(log));
    }

    private static OverrideLogDto ToDto(OverrideLog log)
    {
        var kind = log.Kind.ToString();
        return new OverrideLogDto(log.Id, char.ToLowerInvariant(kind[0]) + kind[1..], log.Reason, log.At);
    }

    public sealed record SaveOverrideRequest(string? Kind, string? Reason);

    public sealed record OverrideLogDto(Guid Id, string Kind, string Reason, DateTime At);
}
