using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// /api/buckets — bucket CRUD. Four buckets are seeded per user; a bucket can only
/// be deleted when it holds no holdings and no cash flows. Percentages are fractions
/// on the wire (0.6 = 60%).
/// </summary>
public sealed class BucketsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/buckets").RequireAuthorization();
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/{id:guid}", Get);
        group.MapPatch("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
    }

    internal static BucketDto ToDto(Bucket b) => new(
        b.Id,
        b.Name,
        b.Kind,
        Pct.ToFraction(b.TargetAllocPct),
        Pct.ToFraction(b.ContributionSplitPct),
        b.HighWaterMarkMinor,
        b.Currency);

    private static async Task<IResult> List(EdgewiseDbContext db, CancellationToken ct)
    {
        var buckets = await db.Buckets.AsNoTracking().OrderBy(b => b.Kind).ThenBy(b => b.Name).ToListAsync(ct);
        return Results.Ok(buckets.Select(ToDto).ToList());
    }

    private static async Task<IResult> Get(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var bucket = await db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw ApiException.NotFound("bucket_not_found", "Bucket not found.");
        return Results.Ok(ToDto(bucket));
    }

    private static async Task<IResult> Create(
        CreateBucketRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw ApiException.BadRequest("validation_error", "name is required.");
        }

        ValidateFraction(request.TargetAllocPct, "targetAllocPct");
        ValidateFraction(request.ContributionSplitPct, "contributionSplitPct");

        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? await db.Users.Select(u => u.BaseCurrency).FirstOrDefaultAsync(ct) ?? "ZAR"
            : request.Currency.Trim().ToUpperInvariant();

        var bucket = new Bucket
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name.Trim(),
            Kind = request.Kind,
            TargetAllocPct = Pct.ToPoints(request.TargetAllocPct),
            ContributionSplitPct = Pct.ToPoints(request.ContributionSplitPct),
            HighWaterMarkMinor = 0,
            Currency = currency,
        };
        db.Buckets.Add(bucket);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/buckets/{bucket.Id}", ToDto(bucket));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateBucketRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var bucket = await db.Buckets.FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw ApiException.NotFound("bucket_not_found", "Bucket not found.");

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                throw ApiException.BadRequest("validation_error", "name cannot be empty.");
            }

            bucket.Name = request.Name.Trim();
        }

        if (request.TargetAllocPct is { } target)
        {
            ValidateFraction(target, "targetAllocPct");
            bucket.TargetAllocPct = Pct.ToPoints(target);
        }

        if (request.ContributionSplitPct is { } split)
        {
            ValidateFraction(split, "contributionSplitPct");
            bucket.ContributionSplitPct = Pct.ToPoints(split);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(bucket));
    }

    private static async Task<IResult> Delete(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var bucket = await db.Buckets.FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw ApiException.NotFound("bucket_not_found", "Bucket not found.");

        if (await db.Holdings.AnyAsync(h => h.BucketId == id, ct))
        {
            throw ApiException.Conflict("bucket_not_empty", "Delete or move the bucket's holdings first.");
        }

        if (await db.CashFlows.AnyAsync(f => f.BucketId == id, ct))
        {
            throw ApiException.Conflict("bucket_not_empty", "The bucket still has cash flows recorded against it.");
        }

        db.Buckets.Remove(bucket);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void ValidateFraction(decimal value, string field)
    {
        if (value is < 0m or > 1m)
        {
            throw ApiException.BadRequest("validation_error", $"{field} must be a fraction between 0 and 1.");
        }
    }
}
