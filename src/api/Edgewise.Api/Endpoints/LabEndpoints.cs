using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

public sealed record LabBarDto(DateTime Ts, decimal O, decimal H, decimal L, decimal C, decimal V);

public sealed record LabInstrumentDto(
    Guid Id, string Symbol, string Name, AssetClass AssetClass, string Currency, string? Exchange);

public sealed record ReplayMarkerDto(DateTime Ts, decimal Price);

public sealed record MaeMfeBandDto(decimal? MaePrice, decimal? MfePrice, decimal? MaePct, decimal? MfePct);

public sealed record TradeReplayDto(
    Guid TradeId,
    Guid InstrumentId,
    Timeframe Timeframe,
    TradeDirection Direction,
    IReadOnlyList<LabBarDto> Bars,
    ReplayMarkerDto Entry,
    ReplayMarkerDto? Exit,
    decimal? StopPrice,
    MaeMfeBandDto? MaeMfeBand);

/// <summary>
/// Lab workspace support routes: stored price bars for the chart workspace, an
/// instrument search over the Instruments table, and per-trade replay data
/// (/api/trades/{id}/replay lives here to avoid clashing with the Journal's
/// TradesEndpoints file).
/// </summary>
public sealed class LabEndpoints : IEndpointModule
{
    private const int ReplayPadBars = 30;

    public void Map(IEndpointRouteBuilder app)
    {
        var lab = app.MapGroup("/api/lab").RequireAuthorization();
        lab.MapGet("/bars", Bars);
        lab.MapGet("/instruments", Instruments);

        app.MapGet("/api/trades/{id:guid}/replay", Replay).RequireAuthorization();
    }

    private static async Task<IResult> Bars(
        EdgewiseDbContext db, Guid instrumentId, Timeframe timeframe, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 400, 10, 2000);
        var bars = await db.PriceBars.AsNoTracking()
            .Where(p => p.InstrumentId == instrumentId && p.Timeframe == timeframe)
            .OrderByDescending(p => p.Ts)
            .Take(take)
            .Select(p => new LabBarDto(p.Ts, p.O, p.H, p.L, p.C, p.V))
            .ToListAsync(ct);
        bars.Reverse();
        return Results.Ok(bars);
    }

    private static async Task<IResult> Instruments(
        EdgewiseDbContext db, string? q, CancellationToken ct)
    {
        var query = db.Instruments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(i => EF.Functions.ILike(i.Symbol, term) || EF.Functions.ILike(i.Name, term));
        }

        var items = await query
            .OrderBy(i => i.Symbol)
            .Take(20)
            .Select(i => new LabInstrumentDto(i.Id, i.Symbol, i.Name, i.AssetClass, i.Currency, i.Exchange))
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> Replay(
        Guid id, EdgewiseDbContext db, Timeframe? timeframe, CancellationToken ct)
    {
        var trade = await db.Trades.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct)
            ?? throw ApiException.NotFound("trade_not_found", "Trade not found.");

        var tf = timeframe ?? Timeframe.D1;
        var windowStart = trade.OpenedAt;
        var windowEnd = trade.ClosedAt ?? trade.OpenedAt;

        var barsQuery = db.PriceBars.AsNoTracking()
            .Where(p => p.InstrumentId == trade.InstrumentId && p.Timeframe == tf);

        var before = await barsQuery
            .Where(p => p.Ts < windowStart)
            .OrderByDescending(p => p.Ts)
            .Take(ReplayPadBars)
            .Select(p => new LabBarDto(p.Ts, p.O, p.H, p.L, p.C, p.V))
            .ToListAsync(ct);
        before.Reverse();

        var during = await barsQuery
            .Where(p => p.Ts >= windowStart && p.Ts <= windowEnd)
            .OrderBy(p => p.Ts)
            .Select(p => new LabBarDto(p.Ts, p.O, p.H, p.L, p.C, p.V))
            .ToListAsync(ct);

        var after = await barsQuery
            .Where(p => p.Ts > windowEnd)
            .OrderBy(p => p.Ts)
            .Take(ReplayPadBars)
            .Select(p => new LabBarDto(p.Ts, p.O, p.H, p.L, p.C, p.V))
            .ToListAsync(ct);

        var bars = before.Concat(during).Concat(after).ToList();
        if (bars.Count == 0)
        {
            throw new ApiException(422, "no_bars",
                $"No stored {tf} bars exist for this trade's instrument around the trade window.");
        }

        decimal? stopPrice = null;
        if (trade.PlanId is Guid planId)
        {
            stopPrice = await db.TradePlans.AsNoTracking()
                .Where(p => p.Id == planId)
                .Select(p => (decimal?)p.StopPrice)
                .SingleOrDefaultAsync(ct);
        }

        MaeMfeBandDto? band = null;
        if (trade.MaePct is not null || trade.MfePct is not null)
        {
            var dir = trade.Direction == TradeDirection.Long ? 1m : -1m;
            var entry = trade.AvgEntryPrice;
            band = new MaeMfeBandDto(
                trade.MaePct is decimal mae ? entry * (1m - (dir * mae / 100m)) : null,
                trade.MfePct is decimal mfe ? entry * (1m + (dir * mfe / 100m)) : null,
                trade.MaePct,
                trade.MfePct);
        }

        return Results.Ok(new TradeReplayDto(
            trade.Id,
            trade.InstrumentId,
            tf,
            trade.Direction,
            bars,
            new ReplayMarkerDto(trade.OpenedAt, trade.AvgEntryPrice),
            trade.ClosedAt is DateTime closedAt && trade.AvgExitPrice is decimal exitPx
                ? new ReplayMarkerDto(closedAt, exitPx)
                : null,
            stopPrice,
            band));
    }
}
