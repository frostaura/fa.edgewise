using Edgewise.Domain.Engines.Adherence;
using Edgewise.Domain.Engines.Analytics;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using DomainEntities = Edgewise.Domain.Entities;

namespace Edgewise.Api.Services.Journal;

public sealed record AnalyticsFilter(DateTime? From, DateTime? To, bool? IsPaper);

public sealed record BiasCardsReport(DateTime AsOf, IReadOnlyList<BiasCard> Cards);

/// <summary>
/// Maps closed trades to the pure analytics engines. This is the substrate the
/// Coach chat tools call as well — every public method is safe to invoke
/// directly from other services (all scoping happens via the DbContext's
/// per-user query filters).
/// </summary>
public sealed class JournalAnalyticsService(EdgewiseDbContext db)
{
    public async Task<ExpectancyReport<string>> ExpectancyAsync(
        string groupBy, AnalyticsFilter filter, CancellationToken ct)
    {
        var stats = await LoadStatsAsync(filter, ct);
        Func<TradeStat, string> selector = groupBy switch
        {
            "instrument" => t => t.InstrumentKey,
            "emotion" => t => t.EmotionTag,
            "dayOfWeek" => t => t.DayOfWeek.ToString(),
            "hourOfDay" => t => t.HourOfDay.ToString("00"),
            _ => t => string.IsNullOrEmpty(t.SetupTag) ? "(none)" : t.SetupTag,
        };
        return RAnalyticsEngine.GroupedExpectancy(stats, selector);
    }

    public async Task<RDistributionReport> RDistributionAsync(AnalyticsFilter filter, CancellationToken ct) =>
        RAnalyticsEngine.RDistribution(await LoadStatsAsync(filter, ct));

    /// <summary>
    /// Drawdown over the bucket's snapshot equity series when snapshots exist, otherwise a
    /// synthetic curve of cumulative realised PnL over closed trades.
    /// </summary>
    public async Task<DrawdownReport> DrawdownAsync(Guid? bucketId, AnalyticsFilter filter, CancellationToken ct)
    {
        var snapshotsQuery = db.Snapshots.AsNoTracking().AsQueryable();
        if (bucketId is not null)
        {
            snapshotsQuery = snapshotsQuery.Where(s => s.BucketId == bucketId);
        }

        var snapshots = await snapshotsQuery.OrderBy(s => s.Date).ToListAsync(ct);
        if (snapshots.Count > 0)
        {
            return RAnalyticsEngine.DrawdownCurve(snapshots.Select(s => new EquityPoint(
                s.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), s.EquityMinor)));
        }

        var trades = await ClosedTrades(bucketId, filter)
            .OrderBy(t => t.ClosedAt)
            .Select(t => new { t.ClosedAt, t.RealisedPnlMinor, t.FeesMinor })
            .ToListAsync(ct);
        var running = 0L;
        var points = trades.Select(t =>
        {
            running += t.RealisedPnlMinor - t.FeesMinor;
            return new EquityPoint(t.ClosedAt!.Value, running);
        });
        return RAnalyticsEngine.DrawdownCurve(points);
    }

    public async Task<IReadOnlyList<CalendarCell>> CalendarHeatmapAsync(
        int? year, AnalyticsFilter filter, CancellationToken ct)
    {
        var stats = await LoadStatsAsync(filter, ct);
        var cells = RAnalyticsEngine.CalendarHeatmap(stats);
        return year is int y ? cells.Where(c => c.Date.Year == y).ToList() : cells;
    }

    public async Task<IReadOnlyList<HourBucket>> SessionsAsync(AnalyticsFilter filter, CancellationToken ct) =>
        RAnalyticsEngine.SessionClock(await LoadStatsAsync(filter, ct));

    public async Task<TiltSignatureReport> TiltAsync(AnalyticsFilter filter, CancellationToken ct) =>
        RAnalyticsEngine.TiltSignature(await LoadStatsAsync(filter, ct));

    public async Task<IReadOnlyList<WeeklyPlanRate>> PtrAsync(AnalyticsFilter filter, CancellationToken ct) =>
        RAnalyticsEngine.PtrTrend(await LoadStatsAsync(filter, ct));

    public async Task<IReadOnlyList<WeeklyAdherence>> AdherenceTrendAsync(
        AnalyticsFilter filter, CancellationToken ct) =>
        RAnalyticsEngine.AdherenceTrend(await LoadStatsAsync(filter, ct));

    public async Task<BiasCardsReport> BiasCardsAsync(AnalyticsFilter filter, CancellationToken ct)
    {
        var trades = await ClosedTrades(null, filter).OrderBy(t => t.ClosedAt).ToListAsync(ct);
        var planIds = trades.Where(t => t.PlanId != null).Select(t => t.PlanId!.Value).Distinct().ToList();
        var plans = await db.TradePlans.AsNoTracking()
            .Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var biasTrades = trades.Select(t =>
        {
            var plan = t.PlanId is Guid pid ? plans.GetValueOrDefault(pid) : null;
            return new BiasTrade
            {
                Key = t.Id.ToString(),
                EntryAt = t.OpenedAt,
                ClosedAt = t.ClosedAt!.Value,
                IsWin = t.RealisedPnlMinor > 0,
                RRealised = t.RRealised,
                HoldingSeconds = t.HoldingSeconds,
                RiskAmount = plan is null ? null : Math.Abs(t.AvgEntryPrice - plan.StopPrice) * t.Qty,
                TriggerPrice = null,
                EntryPrice = t.AvgEntryPrice,
                Direction = t.Direction == DomainEntities.TradeDirection.Long
                    ? TradeDirection.Long
                    : TradeDirection.Short,
            };
        }).ToList();

        var asOf = DateTime.UtcNow;
        return new BiasCardsReport(asOf,
        [
            BiasDetectors.DispositionRatio(biasTrades),
            BiasDetectors.RevengeEntries(biasTrades, asOf),
            BiasDetectors.SizeCreep(biasTrades),
            BiasDetectors.FomoChase(biasTrades, asOf, chaseThresholdPct: 0.01m),
        ]);
    }

    // ------------------------------------------------------------ loading

    /// <summary>Flattens closed trades into engine <see cref="TradeStat"/> records (hours/days in the user's timezone).</summary>
    public async Task<List<TradeStat>> LoadStatsAsync(AnalyticsFilter filter, CancellationToken ct)
    {
        var trades = await ClosedTrades(null, filter).OrderBy(t => t.ClosedAt).ToListAsync(ct);
        if (trades.Count == 0)
        {
            return [];
        }

        var tradeIds = trades.Select(t => t.Id).ToList();
        var planIds = trades.Where(t => t.PlanId != null).Select(t => t.PlanId!.Value).Distinct().ToList();
        var instrumentIds = trades.Select(t => t.InstrumentId).Distinct().ToList();

        var setups = await db.TradePlans.AsNoTracking()
            .Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.SetupTag, ct);
        var symbols = await db.Instruments.AsNoTracking()
            .Where(i => instrumentIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Symbol, ct);
        var scores = (await db.AdherenceResults.AsNoTracking()
                .Where(a => tradeIds.Contains(a.TradeId))
                .ToListAsync(ct))
            .GroupBy(a => a.TradeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.ComputedAt).First().Score);

        var tz = await JournalCommon.GetUserTimeZoneAsync(db, ct);

        return trades.Select(t =>
        {
            var closedLocal = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(t.ClosedAt!.Value, DateTimeKind.Utc), tz);
            return new TradeStat
            {
                ClosedAt = t.ClosedAt!.Value,
                RRealised = t.RRealised,
                RPlanned = t.RPlanned,
                PnlMinor = t.RealisedPnlMinor,
                SetupTag = (t.PlanId is Guid pid ? setups.GetValueOrDefault(pid) : null) ?? "",
                InstrumentKey = symbols.GetValueOrDefault(t.InstrumentId, "?"),
                EmotionTag = t.EmotionTag.ToString(),
                HadPlan = t.PlanId != null,
                AdherenceScore = scores.TryGetValue(t.Id, out var score) ? score : null,
                HoldingSeconds = t.HoldingSeconds,
                IsWin = t.RealisedPnlMinor > 0,
                DayOfWeek = closedLocal.DayOfWeek,
                HourOfDay = closedLocal.Hour,
            };
        }).ToList();
    }

    private IQueryable<DomainEntities.Trade> ClosedTrades(Guid? bucketId, AnalyticsFilter filter)
    {
        var query = db.Trades.AsNoTracking()
            .Where(t => t.Status == DomainEntities.TradeStatus.Closed && t.ClosedAt != null);
        if (bucketId is not null)
        {
            query = query.Where(t => t.BucketId == bucketId);
        }

        if (filter.IsPaper is not null)
        {
            query = query.Where(t => t.IsPaper == filter.IsPaper);
        }

        if (filter.From is not null)
        {
            var from = JournalCommon.AsUtc(filter.From.Value);
            query = query.Where(t => t.ClosedAt >= from);
        }

        if (filter.To is not null)
        {
            var to = JournalCommon.AsUtc(filter.To.Value);
            query = query.Where(t => t.ClosedAt < to);
        }

        return query;
    }
}
