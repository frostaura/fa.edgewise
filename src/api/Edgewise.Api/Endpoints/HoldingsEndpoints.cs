using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// /api/holdings — holdings CRUD with live valuation, nested lot CRUD and the
/// thesis-notes PATCH. Manual assets (Cash/Custom instruments, or any holding
/// with manualGrowthRatePct set) are valued by compounding lot cost at the
/// declared APR instead of quotes.
/// </summary>
public sealed class HoldingsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/holdings").RequireAuthorization();
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/{id:guid}", Get);
        group.MapPatch("/{id:guid}", Update);
        group.MapPatch("/{id:guid}/thesis", UpdateThesis);
        group.MapDelete("/{id:guid}", Delete);

        group.MapGet("/{id:guid}/lots", ListLots);
        group.MapPost("/{id:guid}/lots", CreateLot);
        group.MapPatch("/{id:guid}/lots/{lotId:guid}", UpdateLot);
        group.MapDelete("/{id:guid}/lots/{lotId:guid}", DeleteLot);
    }

    // ---------------------------------------------------------------- holdings

    private static async Task<IResult> List(
        Guid? bucketId, PortfolioService portfolio, CancellationToken ct) =>
        Results.Ok(await portfolio.GetHoldingsAsync(bucketId, ct));

    private static async Task<IResult> Get(Guid id, PortfolioService portfolio, CancellationToken ct) =>
        Results.Ok(await portfolio.GetHoldingAsync(id, ct));

    private static async Task<IResult> Create(
        CreateHoldingRequest request,
        EdgewiseDbContext db,
        ICurrentUser currentUser,
        PortfolioService portfolio,
        CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        _ = await db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BucketId, ct)
            ?? throw ApiException.NotFound("bucket_not_found", "Bucket not found.");
        _ = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.InstrumentId, ct)
            ?? throw ApiException.NotFound("instrument_not_found", "Instrument not found.");

        if (await db.Holdings.AnyAsync(
                h => h.BucketId == request.BucketId && h.InstrumentId == request.InstrumentId, ct))
        {
            throw ApiException.Conflict(
                "holding_exists", "This instrument is already held in that bucket; add lots to it instead.");
        }

        var holding = new Holding
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BucketId = request.BucketId,
            InstrumentId = request.InstrumentId,
            ThesisNotesMd = request.ThesisNotesMd,
            ManualGrowthRatePct = ToStoredGrowthRate(request.ManualGrowthRatePct),
        };
        db.Holdings.Add(holding);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/holdings/{holding.Id}", await portfolio.GetHoldingAsync(holding.Id, ct));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateHoldingRequest request, EdgewiseDbContext db, PortfolioService portfolio, CancellationToken ct)
    {
        var holding = await db.Holdings.FirstOrDefaultAsync(h => h.Id == id, ct)
            ?? throw ApiException.NotFound("holding_not_found", "Holding not found.");

        if (request.BucketId is { } bucketId)
        {
            _ = await db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bucketId, ct)
                ?? throw ApiException.NotFound("bucket_not_found", "Bucket not found.");
            if (bucketId != holding.BucketId && await db.Holdings.AnyAsync(
                    h => h.BucketId == bucketId && h.InstrumentId == holding.InstrumentId, ct))
            {
                throw ApiException.Conflict("holding_exists", "This instrument is already held in that bucket.");
            }

            holding.BucketId = bucketId;
        }

        if (request.ClearManualGrowthRate == true)
        {
            holding.ManualGrowthRatePct = null;
        }
        else if (request.ManualGrowthRatePct is not null)
        {
            holding.ManualGrowthRatePct = ToStoredGrowthRate(request.ManualGrowthRatePct);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(await portfolio.GetHoldingAsync(id, ct));
    }

    private static async Task<IResult> UpdateThesis(
        Guid id, UpdateThesisRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var holding = await db.Holdings.FirstOrDefaultAsync(h => h.Id == id, ct)
            ?? throw ApiException.NotFound("holding_not_found", "Holding not found.");
        holding.ThesisNotesMd = request.ThesisNotesMd;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = holding.Id, thesisNotesMd = holding.ThesisNotesMd });
    }

    private static async Task<IResult> Delete(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var holding = await db.Holdings.FirstOrDefaultAsync(h => h.Id == id, ct)
            ?? throw ApiException.NotFound("holding_not_found", "Holding not found.");

        var lots = await db.Lots.Where(l => l.HoldingId == id).ToListAsync(ct);
        var dividends = await db.Dividends.Where(d => d.HoldingId == id).ToListAsync(ct);
        db.Dividends.RemoveRange(dividends);
        db.Lots.RemoveRange(lots);
        db.Holdings.Remove(holding);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // -------------------------------------------------------------------- lots

    private static async Task<IResult> ListLots(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        await RequireHoldingAsync(db, id, ct);
        var lots = await db.Lots.AsNoTracking()
            .Where(l => l.HoldingId == id)
            .OrderBy(l => l.AcquiredAt)
            .Select(l => new LotDto(l.Id, l.Qty, l.CostMinor, l.CostCurrency, l.AcquiredAt, l.Source, l.TradeId))
            .ToListAsync(ct);
        return Results.Ok(lots);
    }

    private static async Task<IResult> CreateLot(
        Guid id, LotRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var holding = await RequireHoldingAsync(db, id, ct);
        ValidateLot(request);

        var currency = string.IsNullOrWhiteSpace(request.CostCurrency)
            ? await db.Instruments.Where(i => i.Id == holding.InstrumentId).Select(i => i.Currency).FirstAsync(ct)
            : request.CostCurrency.Trim().ToUpperInvariant();

        var lot = new Lot
        {
            Id = Guid.NewGuid(),
            HoldingId = id,
            Qty = request.Qty,
            CostMinor = request.CostMinor,
            CostCurrency = currency,
            AcquiredAt = EnsureUtc(request.AcquiredAt),
            Source = string.IsNullOrWhiteSpace(request.Source) ? "manual" : request.Source.Trim(),
        };
        db.Lots.Add(lot);
        await db.SaveChangesAsync(ct);
        return Results.Created(
            $"/api/holdings/{id}/lots/{lot.Id}",
            new LotDto(lot.Id, lot.Qty, lot.CostMinor, lot.CostCurrency, lot.AcquiredAt, lot.Source, lot.TradeId));
    }

    private static async Task<IResult> UpdateLot(
        Guid id, Guid lotId, LotRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        await RequireHoldingAsync(db, id, ct);
        var lot = await db.Lots.FirstOrDefaultAsync(l => l.Id == lotId && l.HoldingId == id, ct)
            ?? throw ApiException.NotFound("lot_not_found", "Lot not found.");
        ValidateLot(request);

        lot.Qty = request.Qty;
        lot.CostMinor = request.CostMinor;
        if (!string.IsNullOrWhiteSpace(request.CostCurrency))
        {
            lot.CostCurrency = request.CostCurrency.Trim().ToUpperInvariant();
        }

        lot.AcquiredAt = EnsureUtc(request.AcquiredAt);
        if (request.Source is not null)
        {
            lot.Source = request.Source.Trim();
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(
            new LotDto(lot.Id, lot.Qty, lot.CostMinor, lot.CostCurrency, lot.AcquiredAt, lot.Source, lot.TradeId));
    }

    private static async Task<IResult> DeleteLot(Guid id, Guid lotId, EdgewiseDbContext db, CancellationToken ct)
    {
        await RequireHoldingAsync(db, id, ct);
        var lot = await db.Lots.FirstOrDefaultAsync(l => l.Id == lotId && l.HoldingId == id, ct)
            ?? throw ApiException.NotFound("lot_not_found", "Lot not found.");
        db.Lots.Remove(lot);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ----------------------------------------------------------------- helpers

    private static async Task<Holding> RequireHoldingAsync(EdgewiseDbContext db, Guid id, CancellationToken ct) =>
        await db.Holdings.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, ct)
        ?? throw ApiException.NotFound("holding_not_found", "Holding not found.");

    private static void ValidateLot(LotRequest request)
    {
        if (request.Qty <= 0m)
        {
            throw ApiException.BadRequest("validation_error", "qty must be positive.");
        }

        if (request.CostMinor < 0)
        {
            throw ApiException.BadRequest("validation_error", "costMinor cannot be negative.");
        }
    }

    private static decimal? ToStoredGrowthRate(decimal? fraction)
    {
        if (fraction is null)
        {
            return null;
        }

        if (fraction is < -1m or > 2m)
        {
            throw ApiException.BadRequest(
                "validation_error", "manualGrowthRatePct must be a fraction between -1 and 2.");
        }

        return Pct.ToPoints(fraction.Value);
    }

    internal static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
