using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// /api/cashflows — external cash movements feeding TWR/XIRR and snapshots.
/// Requests carry a positive amountMinor; rows are stored signed (deposits and
/// dividend receipts positive, withdrawals negative, transfers as a -/+ pair).
/// Ratchet rows are created only via POST /api/portfolio/ratchet/accept.
/// </summary>
public sealed class CashflowsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cashflows").RequireAuthorization();
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/{id:guid}", Get);
        group.MapPatch("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
    }

    internal static CashFlowDto ToDto(CashFlow f) =>
        new(f.Id, f.BucketId, f.Type, f.AmountMinor, f.Currency, f.At, f.Note);

    private static async Task<IResult> List(
        Guid? bucketId, DateOnly? from, DateOnly? to, EdgewiseDbContext db, CancellationToken ct)
    {
        var query = db.CashFlows.AsNoTracking();
        if (bucketId.HasValue)
        {
            query = query.Where(f => f.BucketId == bucketId.Value);
        }

        if (from.HasValue)
        {
            var fromUtc = from.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(f => f.At >= fromUtc);
        }

        if (to.HasValue)
        {
            var toUtc = to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(f => f.At < toUtc);
        }

        var flows = await query.OrderByDescending(f => f.At).Take(500).ToListAsync(ct);
        return Results.Ok(flows.Select(ToDto).ToList());
    }

    private static async Task<IResult> Get(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var flow = await db.CashFlows.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw ApiException.NotFound("cashflow_not_found", "Cash flow not found.");
        return Results.Ok(ToDto(flow));
    }

    private static async Task<IResult> Create(
        CreateCashFlowRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        if (request.AmountMinor <= 0)
        {
            throw ApiException.BadRequest("validation_error", "amountMinor must be positive.");
        }

        if (request.Type == CashFlowType.Ratchet)
        {
            throw ApiException.BadRequest(
                "validation_error", "Ratchet flows are created via POST /api/portfolio/ratchet/accept.");
        }

        var bucket = await db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BucketId, ct)
            ?? throw ApiException.NotFound("bucket_not_found", "Bucket not found.");
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? bucket.Currency
            : request.Currency.Trim().ToUpperInvariant();
        var at = request.At is { } explicitAt ? HoldingsEndpoints.EnsureUtc(explicitAt) : DateTime.UtcNow;

        if (request.Type == CashFlowType.Transfer)
        {
            if (request.ToBucketId is not { } toBucketId || toBucketId == request.BucketId)
            {
                throw ApiException.BadRequest(
                    "validation_error", "Transfers need a toBucketId different from bucketId.");
            }

            var toBucket = await db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.Id == toBucketId, ct)
                ?? throw ApiException.NotFound("bucket_not_found", "Destination bucket not found.");

            var note = request.Note ?? $"Transfer {bucket.Name} → {toBucket.Name}";
            var outFlow = NewFlow(userId, bucket.Id, CashFlowType.Transfer, -request.AmountMinor, currency, at, note);
            var inFlow = NewFlow(userId, toBucket.Id, CashFlowType.Transfer, request.AmountMinor, currency, at, note);
            db.CashFlows.AddRange(outFlow, inFlow);
            await db.SaveChangesAsync(ct);
            return Results.Created(
                $"/api/cashflows/{outFlow.Id}", new List<CashFlowDto> { ToDto(outFlow), ToDto(inFlow) });
        }

        var signed = request.Type == CashFlowType.Withdrawal ? -request.AmountMinor : request.AmountMinor;
        var flow = NewFlow(userId, bucket.Id, request.Type, signed, currency, at, request.Note);
        db.CashFlows.Add(flow);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/cashflows/{flow.Id}", ToDto(flow));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateCashFlowRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var flow = await db.CashFlows.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw ApiException.NotFound("cashflow_not_found", "Cash flow not found.");

        if (request.At is { } at)
        {
            flow.At = HoldingsEndpoints.EnsureUtc(at);
        }

        if (request.Note is not null)
        {
            flow.Note = request.Note;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(flow));
    }

    private static async Task<IResult> Delete(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var flow = await db.CashFlows.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw ApiException.NotFound("cashflow_not_found", "Cash flow not found.");

        db.CashFlows.Remove(flow);

        // A transfer/ratchet pair is kept consistent: remove the matching opposite leg.
        if (flow.Type is CashFlowType.Transfer or CashFlowType.Ratchet)
        {
            var twin = await db.CashFlows.FirstOrDefaultAsync(
                f => f.Id != flow.Id
                    && f.Type == flow.Type
                    && f.At == flow.At
                    && f.AmountMinor == -flow.AmountMinor
                    && f.Note == flow.Note,
                ct);
            if (twin is not null)
            {
                db.CashFlows.Remove(twin);
            }
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static CashFlow NewFlow(
        Guid userId, Guid bucketId, CashFlowType type, long amountMinor, string currency, DateTime at, string? note) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BucketId = bucketId,
            Type = type,
            AmountMinor = amountMinor,
            Currency = currency,
            At = at,
            Note = note,
        };
}
