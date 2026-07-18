using Edgewise.Domain.Engines.Positions;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using DomainEntities = Edgewise.Domain.Entities;

namespace Edgewise.Api.Services.Journal;

/// <summary>Manual fills plus the match-or-confess inbox over Proposed fills.</summary>
public sealed class FillInboxService(EdgewiseDbContext db, ICurrentUser currentUser)
{
    public async Task<FillDto> CreateManualAsync(CreateFillRequest request, CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);
        if (request.Qty <= 0 || request.Price <= 0)
        {
            throw ApiException.BadRequest("invalid_fill", "qty and price must be positive.");
        }

        var instrument = await db.Instruments.FirstOrDefaultAsync(i => i.Id == request.InstrumentId, ct)
            ?? throw ApiException.BadRequest("instrument_not_found", "Instrument not found.");

        if (request.TradeId is Guid tradeId
            && !await db.Trades.AnyAsync(t => t.Id == tradeId, ct))
        {
            throw ApiException.BadRequest("trade_not_found", "Trade not found.");
        }

        var fill = PlanService.NewManualFill(
            userId,
            new PromoteFillRequest(request.AccountId, request.Side, request.Qty, request.Price,
                request.FeeMinor, request.At),
            request.InstrumentId,
            request.TradeId,
            request.FeeCurrency ?? instrument.Currency);

        db.Fills.Add(fill);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw ApiException.Conflict("duplicate_fill", "An identical fill has already been logged.");
        }

        return JournalCommon.ToDto(fill);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var fill = await db.Fills.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw ApiException.NotFound("fill_not_found", "Fill not found.");
        if (fill.Source != DomainEntities.FillSource.Manual)
        {
            throw ApiException.Conflict("fill_not_manual", "Only manual fills can be deleted.");
        }

        db.Fills.Remove(fill);
        await db.SaveChangesAsync(ct);
    }

    // -------------------------------------------------------------- inbox

    /// <summary>
    /// Proposed fills grouped by (instrument, account) with active-plan suggestions,
    /// open trades to attach to, and a FIFO trade-grouping preview.
    /// </summary>
    public async Task<List<InboxGroupDto>> GetInboxAsync(CancellationToken ct)
    {
        var proposed = await db.Fills.AsNoTracking()
            .Where(f => f.MatchStatus == DomainEntities.MatchStatus.Proposed)
            .OrderBy(f => f.At)
            .ToListAsync(ct);
        if (proposed.Count == 0)
        {
            return [];
        }

        var instrumentIds = proposed.Select(f => f.InstrumentId).Distinct().ToList();
        var accountIds = proposed.Select(f => f.AccountId).Distinct().ToList();

        var instruments = await db.Instruments.AsNoTracking()
            .Where(i => instrumentIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var activePlans = await db.TradePlans.AsNoTracking()
            .Where(p => p.Status == DomainEntities.TradePlanStatus.Active && instrumentIds.Contains(p.InstrumentId))
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
        var openTrades = await db.Trades.AsNoTracking()
            .Where(t => t.Status == DomainEntities.TradeStatus.Open && instrumentIds.Contains(t.InstrumentId))
            .OrderByDescending(t => t.OpenedAt)
            .ToListAsync(ct);

        var groups = new List<InboxGroupDto>();
        foreach (var group in proposed.GroupBy(f => (f.InstrumentId, f.AccountId)))
        {
            var fills = group.ToList();
            var instrument = instruments.GetValueOrDefault(group.Key.InstrumentId);

            var preview = PositionBuilder.Build(
                    instrument?.Symbol ?? group.Key.InstrumentId.ToString(),
                    fills.Select(f => new FillEvent(
                        f.Id.ToString(), f.At,
                        f.Side == DomainEntities.FillSide.Buy ? Side.Buy : Side.Sell,
                        f.Qty, f.Price, f.FeeMinor)),
                    JournalCommon.ToMinor)
                .Select(a => new InboxTradePreviewDto(
                    a.TradeKey,
                    a.Direction == TradeDirection.Long
                        ? DomainEntities.TradeDirection.Long
                        : DomainEntities.TradeDirection.Short,
                    a.OpenedAt, a.ClosedAt, a.Qty, a.AvgEntryPrice, a.AvgExitPrice, a.RealisedPnlMinor,
                    a.FillKeys))
                .ToList();

            groups.Add(new InboxGroupDto(
                group.Key.InstrumentId,
                instrument is null ? null : JournalCommon.ToSummary(instrument),
                group.Key.AccountId,
                accounts.GetValueOrDefault(group.Key.AccountId),
                fills.Select(JournalCommon.ToDto).ToList(),
                activePlans.Where(p => p.InstrumentId == group.Key.InstrumentId)
                    .Select(p => JournalCommon.ToDto(p)).ToList(),
                preview,
                openTrades.Where(t => t.InstrumentId == group.Key.InstrumentId)
                    .Select(t => new OpenTradeSummaryDto(t.Id, t.Direction, t.OpenedAt, t.Qty, t.AvgEntryPrice))
                    .ToList()));
        }

        return groups;
    }

    public async Task<int> GetInboxCountAsync(CancellationToken ct) =>
        await db.Fills.CountAsync(f => f.MatchStatus == DomainEntities.MatchStatus.Proposed, ct);

    /// <summary>
    /// The three-tap match flow: attach to an existing open trade, create a trade from an
    /// active plan (promoting it), or create a standalone trade from the fill.
    /// </summary>
    public async Task<FillDto> MatchAsync(Guid fillId, MatchFillRequest request, CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);
        var fill = await db.Fills.FirstOrDefaultAsync(f => f.Id == fillId, ct)
            ?? throw ApiException.NotFound("fill_not_found", "Fill not found.");
        if (fill.MatchStatus == DomainEntities.MatchStatus.Matched)
        {
            throw ApiException.Conflict("fill_matched", "Fill is already matched.");
        }

        if (request.TradeId is Guid tradeId)
        {
            var trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == tradeId, ct)
                ?? throw ApiException.NotFound("trade_not_found", "Trade not found.");
            AttachFillToTrade(fill, trade);
        }
        else if (request.PlanId is Guid planId)
        {
            var plan = await db.TradePlans.FirstOrDefaultAsync(p => p.Id == planId, ct)
                ?? throw ApiException.NotFound("plan_not_found", "Plan not found.");
            if (plan.InstrumentId != fill.InstrumentId)
            {
                throw ApiException.BadRequest("instrument_mismatch", "Plan and fill instruments differ.");
            }

            var trade = await NewTradeFromFillAsync(userId, fill, plan, ct);
            db.Trades.Add(trade);
            fill.TradeId = trade.Id;
            plan.Status = DomainEntities.TradePlanStatus.Promoted;
        }
        else if (request.CreateTrade)
        {
            var trade = await NewTradeFromFillAsync(userId, fill, null, ct);
            db.Trades.Add(trade);
            fill.TradeId = trade.Id;
        }
        else
        {
            throw ApiException.BadRequest("match_target_required", "Provide planId, tradeId or createTrade.");
        }

        fill.MatchStatus = DomainEntities.MatchStatus.Matched;
        await db.SaveChangesAsync(ct);
        return JournalCommon.ToDto(fill);
    }

    /// <summary>Confess: log the fill as an unplanned trade (or attach it) and mark it Confessed.</summary>
    public async Task<FillDto> ConfessAsync(Guid fillId, CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);
        var fill = await db.Fills.FirstOrDefaultAsync(f => f.Id == fillId, ct)
            ?? throw ApiException.NotFound("fill_not_found", "Fill not found.");

        if (fill.TradeId is null)
        {
            var openTrade = await db.Trades.FirstOrDefaultAsync(t =>
                t.Status == DomainEntities.TradeStatus.Open && t.InstrumentId == fill.InstrumentId, ct);
            if (openTrade is not null)
            {
                AttachFillToTrade(fill, openTrade);
            }
            else
            {
                var trade = await NewTradeFromFillAsync(userId, fill, null, ct);
                db.Trades.Add(trade);
                fill.TradeId = trade.Id;
            }
        }

        fill.MatchStatus = DomainEntities.MatchStatus.Confessed;
        await db.SaveChangesAsync(ct);
        return JournalCommon.ToDto(fill);
    }

    // ------------------------------------------------------------ helpers

    private static void AttachFillToTrade(DomainEntities.Fill fill, DomainEntities.Trade trade)
    {
        fill.TradeId = trade.Id;

        var entrySide = trade.Direction == DomainEntities.TradeDirection.Long
            ? DomainEntities.FillSide.Buy
            : DomainEntities.FillSide.Sell;
        if (fill.Side == entrySide)
        {
            // Scale-in: VWAP the entry.
            var newQty = trade.Qty + fill.Qty;
            trade.AvgEntryPrice = newQty > 0
                ? (trade.AvgEntryPrice * trade.Qty + fill.Price * fill.Qty) / newQty
                : trade.AvgEntryPrice;
            trade.Qty = newQty;
        }

        trade.FeesMinor += fill.FeeMinor;
        if (fill.At < trade.OpenedAt)
        {
            trade.OpenedAt = fill.At;
        }
    }

    private async Task<DomainEntities.Trade> NewTradeFromFillAsync(
        Guid userId, DomainEntities.Fill fill, DomainEntities.TradePlan? plan, CancellationToken ct)
    {
        var bucketId = plan?.BucketId
            ?? await db.Accounts.Where(a => a.Id == fill.AccountId).Select(a => (Guid?)a.BucketId).FirstOrDefaultAsync(ct)
            ?? await db.Buckets.Where(b => b.Kind == DomainEntities.BucketKind.Trading).Select(b => b.Id).FirstOrDefaultAsync(ct);
        var currency = await db.Buckets.Where(b => b.Id == bucketId).Select(b => b.Currency).FirstOrDefaultAsync(ct)
            ?? fill.FeeCurrency;

        return new DomainEntities.Trade
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanId = plan?.Id,
            InstrumentId = fill.InstrumentId,
            BucketId = bucketId,
            AccountId = fill.AccountId == Guid.Empty ? null : fill.AccountId,
            Direction = plan?.Direction
                ?? (fill.Side == DomainEntities.FillSide.Buy
                    ? DomainEntities.TradeDirection.Long
                    : DomainEntities.TradeDirection.Short),
            Status = DomainEntities.TradeStatus.Open,
            OpenedAt = fill.At,
            Qty = fill.Qty,
            AvgEntryPrice = fill.Price,
            FeesMinor = fill.FeeMinor,
            Currency = currency,
            EmotionTag = DomainEntities.EmotionTag.None,
            IsPaper = plan?.IsPaper ?? false,
            PositionMethod = DomainEntities.PositionMethod.Manual,
        };
    }
}
