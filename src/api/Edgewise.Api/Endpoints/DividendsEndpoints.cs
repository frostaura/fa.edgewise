using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Jobs.Portfolio;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// /api/dividends — dividend CRUD per holding plus an income summary. A dividend
/// marked reinvested with a dripQty creates a linked DRIP lot; a cash dividend
/// records a DividendReceipt cash flow on the holding's bucket.
/// </summary>
public sealed class DividendsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dividends").RequireAuthorization();
        group.MapGet("/", List);
        group.MapGet("/summary", Summary);
        group.MapPost("/", Create);
        group.MapPatch("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
    }

    private static DividendDto ToDto(Dividend d) =>
        new(d.Id, d.HoldingId, d.ExDate, d.PayDate, d.AmountMinor, d.Currency, d.Reinvested, d.DripLotId);

    private static async Task<IResult> List(Guid? holdingId, EdgewiseDbContext db, CancellationToken ct)
    {
        var query = db.Dividends.AsNoTracking();
        if (holdingId.HasValue)
        {
            query = query.Where(d => d.HoldingId == holdingId.Value);
        }

        var dividends = await query.OrderByDescending(d => d.PayDate).Take(500).ToListAsync(ct);
        return Results.Ok(dividends.Select(ToDto).ToList());
    }

    private static async Task<IResult> Create(
        CreateDividendRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");
        if (request.AmountMinor <= 0)
        {
            throw ApiException.BadRequest("validation_error", "amountMinor must be positive.");
        }

        var holding = await db.Holdings.AsNoTracking().FirstOrDefaultAsync(h => h.Id == request.HoldingId, ct)
            ?? throw ApiException.NotFound("holding_not_found", "Holding not found.");
        var instrument = await db.Instruments.AsNoTracking().FirstAsync(i => i.Id == holding.InstrumentId, ct);
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? instrument.Currency
            : request.Currency.Trim().ToUpperInvariant();

        var dividend = new Dividend
        {
            Id = Guid.NewGuid(),
            HoldingId = holding.Id,
            ExDate = request.ExDate,
            PayDate = request.PayDate,
            AmountMinor = request.AmountMinor,
            Currency = currency,
            Reinvested = request.Reinvested,
        };

        if (request.Reinvested)
        {
            if (request.DripQty is not > 0m)
            {
                throw ApiException.BadRequest(
                    "validation_error", "A reinvested dividend needs a positive dripQty for the DRIP lot.");
            }

            var dripLot = new Lot
            {
                Id = Guid.NewGuid(),
                HoldingId = holding.Id,
                Qty = request.DripQty.Value,
                CostMinor = request.AmountMinor,
                CostCurrency = currency,
                AcquiredAt = request.PayDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Source = "drip",
            };
            db.Lots.Add(dripLot);
            dividend.DripLotId = dripLot.Id;
        }
        else
        {
            db.CashFlows.Add(new CashFlow
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BucketId = holding.BucketId,
                Type = CashFlowType.DividendReceipt,
                AmountMinor = request.AmountMinor,
                Currency = currency,
                At = request.PayDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Note = $"Dividend {instrument.Symbol}",
            });
        }

        db.Dividends.Add(dividend);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/dividends/{dividend.Id}", ToDto(dividend));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateDividendRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var dividend = await db.Dividends.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw ApiException.NotFound("dividend_not_found", "Dividend not found.");

        if (request.ExDate is { } exDate)
        {
            dividend.ExDate = exDate;
        }

        if (request.PayDate is { } payDate)
        {
            dividend.PayDate = payDate;
        }

        if (request.AmountMinor is { } amount)
        {
            if (amount <= 0)
            {
                throw ApiException.BadRequest("validation_error", "amountMinor must be positive.");
            }

            dividend.AmountMinor = amount;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(dividend));
    }

    private static async Task<IResult> Delete(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var dividend = await db.Dividends.FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? throw ApiException.NotFound("dividend_not_found", "Dividend not found.");

        if (dividend.DripLotId is { } dripLotId)
        {
            var lot = await db.Lots.FirstOrDefaultAsync(l => l.Id == dripLotId, ct);
            if (lot is not null)
            {
                db.Lots.Remove(lot);
            }
        }

        db.Dividends.Remove(dividend);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Per-holding income summary: totals, trailing-12-month income, yield-on-cost
    /// (trailing 12 months over current cost basis) and a naive 12-month projection
    /// (= trailing 12 months). Upcoming ex-dates come from dividend catalyst events
    /// when the calendar vertical has populated them.
    /// </summary>
    private static async Task<IResult> Summary(
        EdgewiseDbContext db,
        ICurrentUser currentUser,
        PortfolioValuationService valuation,
        CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");
        var baseCurrency = await valuation.GetBaseCurrencyAsync(userId, ct);
        var holdingRows = await valuation.ValueHoldingsAsync(userId, ct);
        var dividends = await db.Dividends.AsNoTracking().ToListAsync(ct);
        var byHolding = dividends.GroupBy(d => d.HoldingId).ToDictionary(g => g.Key, g => g.ToList());

        var instrumentIds = holdingRows.Select(r => r.InstrumentId).Distinct().ToList();
        var now = DateTime.UtcNow;
        var upcomingByInstrument = (await db.CatalystEvents.AsNoTracking()
                .Where(c => c.Kind == CatalystKind.Dividend
                    && c.InstrumentId != null
                    && instrumentIds.Contains(c.InstrumentId.Value)
                    && c.At >= now)
                .OrderBy(c => c.At)
                .ToListAsync(ct))
            .GroupBy(c => c.InstrumentId!.Value)
            .ToDictionary(g => g.Key, g => g.Take(3).Select(c => new UpcomingExDateDto(c.At, c.Title)).ToList());

        var yearAgo = DateOnly.FromDateTime(now.AddYears(-1));
        var rows = new List<DividendSummaryRowDto>();
        foreach (var holding in holdingRows)
        {
            var list = byHolding.GetValueOrDefault(holding.HoldingId);
            if (list is null || list.Count == 0)
            {
                continue;
            }

            var total = list.Sum(d => d.AmountMinor);
            var trailing = list.Where(d => d.PayDate >= yearAgo).Sum(d => d.AmountMinor);
            rows.Add(new DividendSummaryRowDto(
                holding.HoldingId,
                holding.InstrumentId,
                holding.Symbol,
                holding.InstrumentName,
                total,
                trailing,
                holding.Value.CostBasisMinor > 0 ? trailing / (decimal)holding.Value.CostBasisMinor : null,
                trailing,
                upcomingByInstrument.GetValueOrDefault(holding.InstrumentId) ?? []));
        }

        rows = rows.OrderByDescending(r => r.Trailing12MoMinor).ToList();
        return Results.Ok(new DividendSummaryDto(
            baseCurrency,
            rows.Sum(r => r.TotalReceivedMinor),
            rows.Sum(r => r.Projected12MoMinor),
            rows));
    }
}
