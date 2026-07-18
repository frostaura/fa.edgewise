using Edgewise.Contracts.Common;
using Edgewise.Domain.Engines.Adherence;
using Edgewise.Domain.Engines.Positions;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using DomainEntities = Edgewise.Domain.Entities;

namespace Edgewise.Api.Services.Journal;

public sealed record TradeListFilter(
    DomainEntities.TradeStatus? Status,
    Guid? InstrumentId,
    Guid? BucketId,
    string? SetupTag,
    DomainEntities.EmotionTag? Emotion,
    bool? IsPaper,
    bool? HasPlan,
    string? Grade,
    DateTime? From,
    DateTime? To,
    string? Search,
    int Page,
    int PageSize);

/// <summary>Application service for the trade log: list, composite detail, close pipeline, rebuild.</summary>
public sealed class TradeService(EdgewiseDbContext db, ICurrentUser currentUser, AdherencePipeline adherence)
{
    // --------------------------------------------------------------- list

    public async Task<PagedResult<TradeListItemDto>> ListAsync(TradeListFilter filter, CancellationToken ct)
    {
        var query = db.Trades.AsNoTracking();

        if (filter.Status is not null)
        {
            query = query.Where(t => t.Status == filter.Status);
        }

        if (filter.InstrumentId is not null)
        {
            query = query.Where(t => t.InstrumentId == filter.InstrumentId);
        }

        if (filter.BucketId is not null)
        {
            query = query.Where(t => t.BucketId == filter.BucketId);
        }

        if (filter.Emotion is not null)
        {
            query = query.Where(t => t.EmotionTag == filter.Emotion);
        }

        if (filter.IsPaper is not null)
        {
            query = query.Where(t => t.IsPaper == filter.IsPaper);
        }

        if (filter.HasPlan is not null)
        {
            query = filter.HasPlan.Value
                ? query.Where(t => t.PlanId != null)
                : query.Where(t => t.PlanId == null);
        }

        if (filter.From is not null)
        {
            var from = JournalCommon.AsUtc(filter.From.Value);
            query = query.Where(t => t.OpenedAt >= from);
        }

        if (filter.To is not null)
        {
            var to = JournalCommon.AsUtc(filter.To.Value);
            query = query.Where(t => t.OpenedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(filter.SetupTag))
        {
            query = query.Where(t => t.PlanId != null &&
                db.TradePlans.Any(p => p.Id == t.PlanId && p.SetupTag == filter.SetupTag));
        }

        if (!string.IsNullOrWhiteSpace(filter.Grade))
        {
            var grade = filter.Grade.ToUpperInvariant();
            query = query.Where(t => db.AdherenceResults
                .Where(a => a.TradeId == t.Id)
                .OrderByDescending(a => a.ComputedAt)
                .Select(a => a.Grade)
                .FirstOrDefault() == grade);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = $"%{filter.Search.Trim()}%";
            query = query.Where(t =>
                db.Instruments.Any(i => i.Id == t.InstrumentId &&
                    (EF.Functions.ILike(i.Symbol, term) || EF.Functions.ILike(i.Name, term)))
                || (t.PlanId != null && db.TradePlans.Any(p =>
                    p.Id == t.PlanId && p.SetupTag != null && EF.Functions.ILike(p.SetupTag, term))));
        }

        var total = await query.LongCountAsync(ct);
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var trades = await query
            .OrderByDescending(t => t.OpenedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var tradeIds = trades.Select(t => t.Id).ToList();
        var instrumentIds = trades.Select(t => t.InstrumentId).Distinct().ToList();
        var planIds = trades.Where(t => t.PlanId != null).Select(t => t.PlanId!.Value).Distinct().ToList();

        var instruments = await db.Instruments.AsNoTracking()
            .Where(i => instrumentIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, ct);
        var setups = await db.TradePlans.AsNoTracking()
            .Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.SetupTag, ct);
        var adherenceByTrade = (await db.AdherenceResults.AsNoTracking()
                .Where(a => tradeIds.Contains(a.TradeId))
                .ToListAsync(ct))
            .GroupBy(a => a.TradeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.ComputedAt).First());
        var tagsByTrade = (await db.TradeTags.AsNoTracking()
                .Where(tt => tradeIds.Contains(tt.TradeId))
                .Join(db.Tags, tt => tt.TagId, tag => tag.Id, (tt, tag) => new { tt.TradeId, tag.Name })
                .ToListAsync(ct))
            .GroupBy(x => x.TradeId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).OrderBy(n => n).ToList());

        var items = trades.Select(t =>
        {
            var latest = adherenceByTrade.GetValueOrDefault(t.Id);
            return new TradeListItemDto(
                t.Id, t.PlanId, t.InstrumentId,
                instruments.GetValueOrDefault(t.InstrumentId)?.Symbol ?? "?",
                t.Direction, t.Status, t.OpenedAt, t.ClosedAt, t.Qty, t.AvgEntryPrice, t.AvgExitPrice,
                t.RealisedPnlMinor, t.Currency, t.RRealised, t.EmotionTag, t.IsPaper,
                t.PlanId is Guid pid ? setups.GetValueOrDefault(pid) : null,
                latest?.Score, latest?.Grade,
                tagsByTrade.GetValueOrDefault(t.Id) ?? []);
        }).ToList();

        return new PagedResult<TradeListItemDto>(items, page, pageSize, total);
    }

    // ------------------------------------------------------------- detail

    public async Task<TradeDetailDto> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var trade = await db.Trades.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw ApiException.NotFound("trade_not_found", "Trade not found.");

        var instrument = await db.Instruments.AsNoTracking().FirstOrDefaultAsync(i => i.Id == trade.InstrumentId, ct);
        var plan = trade.PlanId is Guid planId
            ? await db.TradePlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct)
            : null;
        var versions = plan is null
            ? []
            : await db.TradePlanVersions.AsNoTracking()
                .Where(v => v.PlanId == plan.Id)
                .OrderBy(v => v.Version)
                .Select(v => new PlanVersionDto(v.Version, v.FieldsJson, v.At))
                .ToListAsync(ct);
        var fills = await db.Fills.AsNoTracking()
            .Where(f => f.TradeId == trade.Id)
            .OrderBy(f => f.At)
            .ToListAsync(ct);
        var entries = await db.JournalEntries.AsNoTracking()
            .Where(j => j.TradeId == trade.Id)
            .OrderByDescending(j => j.At)
            .Select(j => new JournalEntryDto(j.Id, j.NotesMd, j.At))
            .ToListAsync(ct);
        var adherenceHistory = await db.AdherenceResults.AsNoTracking()
            .Where(a => a.TradeId == trade.Id)
            .OrderByDescending(a => a.ComputedAt)
            .ToListAsync(ct);
        var tags = await db.TradeTags.AsNoTracking()
            .Where(tt => tt.TradeId == trade.Id)
            .Join(db.Tags, tt => tt.TagId, tag => tag.Id, (tt, tag) => tag.Name)
            .OrderBy(n => n)
            .ToListAsync(ct);
        var insights = await db.Insights.AsNoTracking()
            .Where(i => i.TradeId == trade.Id)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InsightDto(i.Id, i.Type, i.ContentJson, i.CreatedAt))
            .ToListAsync(ct);

        return new TradeDetailDto(
            JournalCommon.ToDto(trade, instrument),
            plan is null ? null : JournalCommon.ToDto(plan),
            versions,
            fills.Select(JournalCommon.ToDto).ToList(),
            entries,
            adherenceHistory.Count > 0 ? JournalCommon.ToDto(adherenceHistory[0]) : null,
            adherenceHistory.Select(JournalCommon.ToDto).ToList(),
            tags,
            insights.Count > 0 ? insights : null);
    }

    // -------------------------------------------------------------- patch

    public async Task<TradeDetailDto> PatchAsync(Guid id, PatchTradeRequest request, CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);
        var trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw ApiException.NotFound("trade_not_found", "Trade not found.");

        if (request.EmotionTag is not null)
        {
            trade.EmotionTag = request.EmotionTag.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            db.JournalEntries.Add(new DomainEntities.JournalEntry
            {
                Id = Guid.NewGuid(),
                TradeId = trade.Id,
                Trade = trade,
                NotesMd = request.Notes,
                At = DateTime.UtcNow,
            });
        }

        if (request.Tags is not null)
        {
            await ReplaceTagsAsync(trade, request.Tags, userId, ct);
        }

        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, ct);
    }

    // -------------------------------------------------------------- close

    /// <summary>
    /// Finalises a trade: realises PnL from fills (or the supplied close price), computes
    /// holding time, realised R against the planned risk, best-effort MAE/MFE from stored
    /// price bars, and runs the adherence pipeline (appending an AdherenceResult).
    /// </summary>
    public async Task<TradeDetailDto> CloseAsync(Guid id, CloseTradeRequest request, CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);
        var trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw ApiException.NotFound("trade_not_found", "Trade not found.");
        if (trade.Status == DomainEntities.TradeStatus.Closed)
        {
            throw ApiException.Conflict("trade_closed", "Trade is already closed.");
        }

        var exitSide = trade.Direction == DomainEntities.TradeDirection.Long
            ? DomainEntities.FillSide.Sell
            : DomainEntities.FillSide.Buy;

        if (request.ExitFills is { Count: > 0 } exitFills)
        {
            var currency = string.IsNullOrEmpty(trade.Currency) ? "ZAR" : trade.Currency;
            for (var i = 0; i < exitFills.Count; i++)
            {
                var fill = exitFills[i] with { Side = exitSide, AccountId = exitFills[i].AccountId ?? trade.AccountId };
                db.Fills.Add(PlanService.NewManualFill(userId, fill, trade.InstrumentId, trade.Id, currency, 1000 + i));
                trade.FeesMinor += fill.FeeMinor;
            }

            await db.SaveChangesAsync(ct);
        }

        var fills = await db.Fills.AsNoTracking()
            .Where(f => f.TradeId == trade.Id)
            .OrderBy(f => f.At)
            .ToListAsync(ct);
        var exits = fills.Where(f => f.Side == exitSide).ToList();

        decimal avgExit;
        DateTime closedAt;
        if (exits.Count > 0)
        {
            var exitQty = exits.Sum(f => f.Qty);
            avgExit = exits.Sum(f => f.Qty * f.Price) / exitQty;
            closedAt = exits.Max(f => f.At);
        }
        else if (request.AvgExitPrice is decimal price && price > 0)
        {
            avgExit = price;
            closedAt = request.ClosedAt is DateTime at ? JournalCommon.AsUtc(at) : DateTime.UtcNow;
        }
        else
        {
            throw ApiException.BadRequest("exit_required", "Provide exitFills or avgExitPrice.");
        }

        if (closedAt < trade.OpenedAt)
        {
            throw ApiException.BadRequest("invalid_close_time", "Close time is before the trade opened.");
        }

        var dirSign = trade.Direction == DomainEntities.TradeDirection.Long ? 1m : -1m;
        trade.AvgExitPrice = avgExit;
        trade.ClosedAt = closedAt;
        trade.Status = DomainEntities.TradeStatus.Closed;
        trade.RealisedPnlMinor = JournalCommon.ToMinor((avgExit - trade.AvgEntryPrice) * trade.Qty * dirSign);
        trade.HoldingSeconds = (long)(closedAt - trade.OpenedAt).TotalSeconds;

        // Realised R against planned risk (|entry - plan stop| x qty); null when unplanned.
        var plan = trade.PlanId is Guid planId
            ? await db.TradePlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct)
            : null;
        if (plan is not null)
        {
            var riskMinor = JournalCommon.ToMinor(Math.Abs(trade.AvgEntryPrice - plan.StopPrice) * trade.Qty);
            trade.RRealised = riskMinor > 0 ? decimal.Round((decimal)trade.RealisedPnlMinor / riskMinor, 4) : null;
            trade.RPlanned ??= PlanService.ComputePlannedR(plan, trade.AvgEntryPrice);
        }

        // Best-effort MAE/MFE from stored H1 bars over the holding window; skipped when absent.
        try
        {
            var bars = await db.PriceBars.AsNoTracking()
                .Where(b => b.InstrumentId == trade.InstrumentId
                    && b.Timeframe == DomainEntities.Timeframe.H1
                    && b.Ts >= trade.OpenedAt && b.Ts <= closedAt)
                .Select(b => new { b.H, b.L })
                .ToListAsync(ct);
            if (bars.Count > 0)
            {
                var (mae, mfe) = PositionBuilder.ComputeMaeMfe(
                    trade.AvgEntryPrice,
                    trade.Direction == DomainEntities.TradeDirection.Long
                        ? Domain.Engines.Positions.TradeDirection.Long
                        : Domain.Engines.Positions.TradeDirection.Short,
                    bars.Select(b => (b.H, b.L)).ToList());
                trade.MaePct = mae;
                trade.MfePct = mfe;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Market data is a soft dependency: closing must not fail on it.
        }

        // Auto-classify a stop hit when the caller did not classify the exit.
        var exitRule = request.ExitRule ?? ClassifyExit(trade, plan, avgExit);

        await db.SaveChangesAsync(ct);
        await adherence.ComputeAndPersistAsync(trade, exitRule, ct);
        return await GetDetailAsync(id, ct);
    }

    public async Task<TradeDetailDto> ReopenAsync(Guid id, CancellationToken ct)
    {
        var trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw ApiException.NotFound("trade_not_found", "Trade not found.");
        trade.Status = DomainEntities.TradeStatus.Open;
        trade.ClosedAt = null;
        trade.AvgExitPrice = null;
        trade.RealisedPnlMinor = 0;
        trade.RRealised = null;
        trade.HoldingSeconds = null;
        await db.SaveChangesAsync(ct);
        return await GetDetailAsync(id, ct);
    }

    // ------------------------------------------------------------ rebuild

    /// <summary>
    /// Escape hatch: re-derives trades for one (account, instrument) from its Matched fills
    /// via the FIFO PositionBuilder. Existing derived trades are replaced only when they have
    /// no manual edits (no plan, no journal entries, no tags, no adherence results and a
    /// neutral emotion tag); edited trades are left untouched and their fills excluded.
    /// </summary>
    public async Task<RebuildTradesResponse> RebuildAsync(RebuildTradesRequest request, CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);

        var fills = await db.Fills
            .Where(f => f.AccountId == request.AccountId
                && f.InstrumentId == request.InstrumentId
                && f.MatchStatus != DomainEntities.MatchStatus.Proposed)
            .OrderBy(f => f.At)
            .ToListAsync(ct);
        if (fills.Count == 0)
        {
            return new RebuildTradesResponse(0, 0, 0);
        }

        var tradeIds = fills.Where(f => f.TradeId != null).Select(f => f.TradeId!.Value).Distinct().ToList();
        var trades = await db.Trades.Where(t => tradeIds.Contains(t.Id)).ToListAsync(ct);

        var editedTradeIds = new HashSet<Guid>();
        foreach (var trade in trades)
        {
            var edited = trade.PlanId is not null
                || trade.EmotionTag != DomainEntities.EmotionTag.None
                || await db.JournalEntries.AnyAsync(j => j.TradeId == trade.Id, ct)
                || await db.TradeTags.AnyAsync(tt => tt.TradeId == trade.Id, ct)
                || await db.AdherenceResults.AnyAsync(a => a.TradeId == trade.Id, ct);
            if (edited)
            {
                editedTradeIds.Add(trade.Id);
            }
        }

        var rebuildFills = fills.Where(f => f.TradeId is null || !editedTradeIds.Contains(f.TradeId.Value)).ToList();
        var removable = trades.Where(t => !editedTradeIds.Contains(t.Id)).ToList();

        var instrument = await db.Instruments.AsNoTracking().FirstAsync(i => i.Id == request.InstrumentId, ct);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AccountId, ct);
        var bucketId = account?.BucketId
            ?? removable.FirstOrDefault()?.BucketId
            ?? await db.Buckets.Where(b => b.Kind == DomainEntities.BucketKind.Trading).Select(b => b.Id).FirstOrDefaultAsync(ct);
        var currency = removable.FirstOrDefault()?.Currency
            ?? await db.Buckets.Where(b => b.Id == bucketId).Select(b => b.Currency).FirstOrDefaultAsync(ct)
            ?? instrument.Currency;

        var aggregates = PositionBuilder.Build(
            instrument.Symbol,
            rebuildFills.Select(f => new FillEvent(
                f.Id.ToString(),
                f.At,
                f.Side == DomainEntities.FillSide.Buy ? Side.Buy : Side.Sell,
                f.Qty,
                f.Price,
                f.FeeMinor)),
            JournalCommon.ToMinor);

        db.Trades.RemoveRange(removable);

        var fillsById = rebuildFills.ToDictionary(f => f.Id.ToString());
        var created = 0;
        foreach (var aggregate in aggregates)
        {
            var trade = new DomainEntities.Trade
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PlanId = null,
                InstrumentId = request.InstrumentId,
                BucketId = bucketId,
                AccountId = request.AccountId,
                Direction = aggregate.Direction == Domain.Engines.Positions.TradeDirection.Long
                    ? DomainEntities.TradeDirection.Long
                    : DomainEntities.TradeDirection.Short,
                Status = aggregate.Status == Domain.Engines.Positions.TradeStatus.Closed
                    ? DomainEntities.TradeStatus.Closed
                    : DomainEntities.TradeStatus.Open,
                OpenedAt = aggregate.OpenedAt,
                ClosedAt = aggregate.ClosedAt,
                Qty = aggregate.Qty,
                AvgEntryPrice = aggregate.AvgEntryPrice,
                AvgExitPrice = aggregate.AvgExitPrice,
                RealisedPnlMinor = aggregate.RealisedPnlMinor,
                FeesMinor = aggregate.FeesMinor,
                FundingMinor = aggregate.FundingMinor,
                Currency = currency,
                HoldingSeconds = aggregate.ClosedAt is DateTime c ? (long)(c - aggregate.OpenedAt).TotalSeconds : null,
                EmotionTag = DomainEntities.EmotionTag.None,
                IsPaper = false,
                PositionMethod = DomainEntities.PositionMethod.Fifo,
            };
            db.Trades.Add(trade);
            created++;

            foreach (var key in aggregate.FillKeys)
            {
                if (fillsById.TryGetValue(key, out var fill))
                {
                    fill.TradeId = trade.Id;
                    fill.MatchStatus = fill.MatchStatus == DomainEntities.MatchStatus.Confessed
                        ? DomainEntities.MatchStatus.Confessed
                        : DomainEntities.MatchStatus.Matched;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return new RebuildTradesResponse(removable.Count, created, editedTradeIds.Count);
    }

    // ---------------------------------------------------------- bulk tags

    public async Task<int> BulkTagsAsync(BulkTagsRequest request, CancellationToken ct)
    {
        var userId = JournalCommon.RequireUserId(currentUser);
        var trades = await db.Trades.Where(t => request.TradeIds.Contains(t.Id)).ToListAsync(ct);

        var addNames = (request.AddTags ?? []).Select(NormalizeTag).Where(n => n.Length > 0).Distinct().ToList();
        var removeNames = (request.RemoveTags ?? []).Select(NormalizeTag).Where(n => n.Length > 0).Distinct().ToList();

        var tags = await EnsureTagsAsync(addNames, userId, ct);
        var removeTagIds = await db.Tags.Where(t => removeNames.Contains(t.Name)).Select(t => t.Id).ToListAsync(ct);

        var tradeIds = trades.Select(t => t.Id).ToList();
        var existing = await db.TradeTags.Where(tt => tradeIds.Contains(tt.TradeId)).ToListAsync(ct);

        foreach (var trade in trades)
        {
            foreach (var tag in tags)
            {
                if (!existing.Any(tt => tt.TradeId == trade.Id && tt.TagId == tag.Id))
                {
                    db.TradeTags.Add(new DomainEntities.TradeTag { TradeId = trade.Id, Trade = trade, TagId = tag.Id });
                }
            }
        }

        if (removeTagIds.Count > 0)
        {
            db.TradeTags.RemoveRange(existing.Where(tt => removeTagIds.Contains(tt.TagId)));
        }

        await db.SaveChangesAsync(ct);
        return trades.Count;
    }

    // ------------------------------------------------------------ helpers

    internal static ExitRuleOutcome ClassifyExit(
        DomainEntities.Trade trade, DomainEntities.TradePlan? plan, decimal avgExit)
    {
        if (plan is null)
        {
            return ExitRuleOutcome.MatchedRule;
        }

        var stopHit = trade.Direction == DomainEntities.TradeDirection.Long
            ? avgExit <= plan.StopPrice
            : avgExit >= plan.StopPrice;
        return stopHit ? ExitRuleOutcome.StopHit : ExitRuleOutcome.MatchedRule;
    }

    private async Task ReplaceTagsAsync(
        DomainEntities.Trade trade, List<string> tagNames, Guid userId, CancellationToken ct)
    {
        var names = tagNames.Select(NormalizeTag).Where(n => n.Length > 0).Distinct().ToList();
        var tags = await EnsureTagsAsync(names, userId, ct);
        var keepIds = tags.Select(t => t.Id).ToHashSet();

        var existing = await db.TradeTags.Where(tt => tt.TradeId == trade.Id).ToListAsync(ct);
        db.TradeTags.RemoveRange(existing.Where(tt => !keepIds.Contains(tt.TagId)));
        foreach (var tag in tags)
        {
            if (!existing.Any(tt => tt.TagId == tag.Id))
            {
                db.TradeTags.Add(new DomainEntities.TradeTag { TradeId = trade.Id, Trade = trade, TagId = tag.Id });
            }
        }
    }

    private async Task<List<DomainEntities.Tag>> EnsureTagsAsync(
        List<string> names, Guid userId, CancellationToken ct)
    {
        if (names.Count == 0)
        {
            return [];
        }

        var existing = await db.Tags.Where(t => names.Contains(t.Name)).ToListAsync(ct);
        foreach (var name in names.Where(n => !existing.Any(t => t.Name == n)))
        {
            var tag = new DomainEntities.Tag { Id = Guid.NewGuid(), UserId = userId, Name = name };
            db.Tags.Add(tag);
            existing.Add(tag);
        }

        return existing;
    }

    private static string NormalizeTag(string name) => name.Trim().ToLowerInvariant();
}
