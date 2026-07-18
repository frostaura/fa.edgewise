using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

public sealed record ZoneDto(
    Guid Id,
    Guid InstrumentId,
    decimal PriceLow,
    decimal PriceHigh,
    int Strength,
    Timeframe SourceTimeframe,
    DateTime CreatedAt,
    bool Archived);

public sealed record CreateZoneRequest(
    Guid InstrumentId,
    decimal PriceLow,
    decimal PriceHigh,
    int Strength,
    Timeframe SourceTimeframe);

public sealed record UpdateZoneRequest(
    decimal? PriceLow,
    decimal? PriceHigh,
    int? Strength,
    Timeframe? SourceTimeframe);

/// <summary>
/// Support/resistance zones drawn in the Lab chart workspace. Consumed by the
/// charts (drill-down + lab) and future zone-touch alerts.
/// </summary>
public sealed class ZonesEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/zones").RequireAuthorization();

        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapPost("/{id:guid}/archive", ToggleArchive);
        group.MapDelete("/{id:guid}", Delete);
    }

    private static async Task<IResult> List(
        EdgewiseDbContext db, Guid? instrumentId, bool? includeArchived, CancellationToken ct)
    {
        var query = db.Zones.AsNoTracking();
        if (instrumentId is Guid iid)
        {
            query = query.Where(z => z.InstrumentId == iid);
        }

        if (includeArchived is not true)
        {
            query = query.Where(z => !z.Archived);
        }

        var zones = await query
            .OrderByDescending(z => z.Strength)
            .ThenByDescending(z => z.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(zones.Select(ToDto).ToList());
    }

    private static async Task<IResult> Create(
        CreateZoneRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        ValidatePrices(request.PriceLow, request.PriceHigh, request.Strength);

        var instrumentExists = await db.Instruments.AnyAsync(i => i.Id == request.InstrumentId, ct);
        if (!instrumentExists)
        {
            throw ApiException.NotFound("instrument_not_found", "The instrument does not exist.");
        }

        var zone = new Zone
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InstrumentId = request.InstrumentId,
            PriceLow = request.PriceLow,
            PriceHigh = request.PriceHigh,
            Strength = request.Strength,
            SourceTimeframe = request.SourceTimeframe,
            CreatedAt = DateTime.UtcNow,
            Archived = false,
        };
        db.Zones.Add(zone);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/zones/{zone.Id}", ToDto(zone));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateZoneRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var zone = await db.Zones.SingleOrDefaultAsync(z => z.Id == id, ct)
            ?? throw ApiException.NotFound("zone_not_found", "Zone not found.");

        var low = request.PriceLow ?? zone.PriceLow;
        var high = request.PriceHigh ?? zone.PriceHigh;
        var strength = request.Strength ?? zone.Strength;
        ValidatePrices(low, high, strength);

        zone.PriceLow = low;
        zone.PriceHigh = high;
        zone.Strength = strength;
        if (request.SourceTimeframe is Timeframe tf)
        {
            zone.SourceTimeframe = tf;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(zone));
    }

    private static async Task<IResult> ToggleArchive(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var zone = await db.Zones.SingleOrDefaultAsync(z => z.Id == id, ct)
            ?? throw ApiException.NotFound("zone_not_found", "Zone not found.");

        zone.Archived = !zone.Archived;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(zone));
    }

    private static async Task<IResult> Delete(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var zone = await db.Zones.SingleOrDefaultAsync(z => z.Id == id, ct)
            ?? throw ApiException.NotFound("zone_not_found", "Zone not found.");

        db.Zones.Remove(zone);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void ValidatePrices(decimal low, decimal high, int strength)
    {
        if (low <= 0m)
        {
            throw ApiException.BadRequest("invalid_price", "priceLow must be greater than 0.");
        }

        if (high <= low)
        {
            throw ApiException.BadRequest("invalid_price", "priceHigh must be greater than priceLow.");
        }

        if (strength is < 1 or > 5)
        {
            throw ApiException.BadRequest("invalid_strength", "strength must be between 1 and 5.");
        }
    }

    internal static ZoneDto ToDto(Zone z) => new(
        z.Id, z.InstrumentId, z.PriceLow, z.PriceHigh, z.Strength, z.SourceTimeframe, z.CreatedAt, z.Archived);
}
