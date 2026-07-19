using System.ComponentModel;
using System.Text.Json;
using Edgewise.Api.Services.Journal;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Edgewise.Api.Mcp;

/// <summary>
/// Journal tools: read the trade log and journal new intent/activity. All queries
/// are scoped to the authenticated user by the DbContext's global query filters
/// (MCP requests carry a Bearer JWT or ew_ personal access token).
/// This surface only journals — it never places, modifies or executes orders.
/// </summary>
[McpServerToolType]
public sealed class JournalTools(
    EdgewiseDbContext db,
    TradeService tradeService,
    PlanService planService,
    FillInboxService fillInbox)
{
    [McpServerTool(Name = "journal_list_trades")]
    [Description("Lists the user's journalled trades, newest first, as compact rows " +
        "(direction, status, quantity, entry/exit, realised P&L in minor units, R multiple, adherence grade, tags).")]
    public Task<string> ListTrades(
        [Description("Optional status filter: 'open' or 'closed'. Omit for all trades.")] string? status = null,
        [Description("Optional instrument symbol filter, e.g. 'BTC-USD'. Omit for all instruments.")] string? instrument = null,
        [Description("Maximum number of trades to return (1-100). Default 20.")] int limit = 20,
        CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
    {
        TradeStatus? statusFilter = string.IsNullOrWhiteSpace(status)
            ? null
            : McpToolSupport.ParseEnum<TradeStatus>(status, "status");
        Guid? instrumentId = string.IsNullOrWhiteSpace(instrument)
            ? null
            : (await McpToolSupport.ResolveInstrumentAsync(db, instrument, ct)).Id;

        var page = await tradeService.ListAsync(
            new TradeListFilter(
                statusFilter, instrumentId, BucketId: null, SetupTag: null, Emotion: null, IsPaper: null,
                HasPlan: null, Grade: null, From: null, To: null, Search: null,
                Page: 1, PageSize: Math.Clamp(limit, 1, 100)),
            ct);

        return new
        {
            totalCount = page.TotalCount,
            returned = page.Items.Count,
            trades = page.Items.Select(t => new
            {
                t.Id,
                symbol = t.InstrumentSymbol,
                t.Direction,
                t.Status,
                t.OpenedAt,
                t.ClosedAt,
                t.Qty,
                t.AvgEntryPrice,
                t.AvgExitPrice,
                realisedPnlMinor = t.RealisedPnlMinor,
                t.Currency,
                rRealised = t.RRealised,
                t.SetupTag,
                t.AdherenceScore,
                t.AdherenceGrade,
                emotion = t.EmotionTag,
                t.IsPaper,
                tags = t.Tags,
            }),
        };
    });

    [McpServerTool(Name = "journal_get_trade")]
    [Description("Fetches one trade's full composite: the trade, its plan (with version history), " +
        "fills, journal entries, adherence results, tags and any coach insights.")]
    public Task<string> GetTrade(
        [Description("The trade id (GUID) as returned by journal_list_trades.")] string tradeId,
        CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
        await tradeService.GetDetailAsync(McpToolSupport.ParseId(tradeId, "tradeId"), ct));

    [McpServerTool(Name = "journal_create_plan")]
    [Description("Creates a Draft trade plan in the journal (setup, trigger, stop, target note, size, invalidation). " +
        "This is journaling only: nothing is ever executed at a broker or exchange, and the draft stays inactive " +
        "until the user reviews and activates it in the app.")]
    public Task<string> CreatePlan(
        [Description("Instrument symbol the plan is for, e.g. 'BTC-USD'.")] string instrumentSymbol,
        [Description("Planned direction: 'long' or 'short'.")] string direction,
        [Description("Setup tag naming the playbook setup, e.g. 'trend-pullback'.")] string setupTag,
        [Description("The entry trigger in plain language, e.g. 'H4 close back above the 20EMA'.")] string trigger,
        [Description("The stop-loss price (must be positive).")] decimal stopPrice,
        [Description("Free-text target description, e.g. 'take half at 2R, trail the rest'.")] string targetNote,
        [Description("Planned position size in units of the instrument.")] decimal sizeQty,
        [Description("What invalidates the idea, e.g. 'loses the 20EMA on a daily close'.")] string invalidationNote,
        CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
    {
        var instrument = await McpToolSupport.ResolveInstrumentAsync(db, instrumentSymbol, ct);
        var parsedDirection = McpToolSupport.ParseEnum<TradeDirection>(direction, "direction");

        var bucketId = await FirstBucketIdAsync(ct);
        var targetRuleJson = string.IsNullOrWhiteSpace(targetNote)
            ? null
            : JsonSerializer.Serialize(new { note = targetNote.Trim() }, McpToolSupport.Json);

        var created = await planService.CreateAsync(
            new CreatePlanRequest(
                TemplateId: null,
                InstrumentId: instrument.Id,
                BucketId: bucketId,
                RiskProfileId: null,
                Direction: parsedDirection,
                SetupTag: string.IsNullOrWhiteSpace(setupTag) ? null : setupTag.Trim(),
                TriggerText: string.IsNullOrWhiteSpace(trigger) ? null : trigger.Trim(),
                StopPrice: stopPrice,
                TargetRuleJson: targetRuleJson,
                SizeQty: sizeQty,
                SizeOverridden: null,
                InvalidationNote: string.IsNullOrWhiteSpace(invalidationNote) ? null : invalidationNote.Trim(),
                IsPaper: false,
                ChecklistConfirmedJson: null,
                CockpitCheckJson: null,
                EntryPrice: null),
            ct);

        // Agent-created plans stay Draft: the user activates them after review.
        var draft = await planService.PatchAsync(
            created.Id,
            new PatchPlanRequest(null, null, null, null, null, null, null, null, TradePlanStatus.Draft),
            ct);

        return new
        {
            message = "Draft plan journalled. Nothing was executed; the user must review and activate it in Edgewise.",
            plan = draft,
        };
    });

    [McpServerTool(Name = "journal_quick_log")]
    [Description("Quick-logs an execution the user already made elsewhere: records a manual fill and confesses it " +
        "into an (unplanned) trade, attaching to an open trade on the same instrument when one exists. " +
        "Journaling only — no order is placed anywhere.")]
    public Task<string> QuickLog(
        [Description("Instrument symbol that was traded, e.g. 'ETH-USD'.")] string instrumentSymbol,
        [Description("Fill side: 'buy' or 'sell'.")] string side,
        [Description("Filled quantity in units of the instrument (must be positive).")] decimal qty,
        [Description("Fill price (must be positive).")] decimal price,
        [Description("Optional ISO-8601 execution timestamp, e.g. '2026-07-18T09:30:00Z'. Defaults to now (UTC).")] string? at = null,
        CancellationToken ct = default) => McpToolSupport.RunAsync(async () =>
    {
        var instrument = await McpToolSupport.ResolveInstrumentAsync(db, instrumentSymbol, ct);
        var parsedSide = McpToolSupport.ParseEnum<FillSide>(side, "side");
        var atUtc = McpToolSupport.ParseTimestamp(at, "at");

        var fill = await fillInbox.CreateManualAsync(
            new CreateFillRequest(
                AccountId: null, InstrumentId: instrument.Id, Side: parsedSide, Qty: qty, Price: price,
                FeeMinor: 0, FeeCurrency: null, At: atUtc, TradeId: null),
            ct);
        var confessed = await fillInbox.ConfessAsync(fill.Id, ct);

        return new
        {
            message = "Fill journalled and confessed as an unplanned execution.",
            fill = confessed,
            tradeId = confessed.TradeId,
        };
    });

    /// <summary>Plans default into the user's Trading bucket (falling back to any bucket).</summary>
    private async Task<Guid> FirstBucketIdAsync(CancellationToken ct)
    {
        var buckets = await db.Buckets.AsNoTracking().ToListAsync(ct);
        return (buckets.FirstOrDefault(b => b.Kind == BucketKind.Trading) ?? buckets.FirstOrDefault())?.Id
            ?? throw ApiException.NotFound(
                "no_bucket", "No portfolio bucket exists to attach the plan to; create one in Edgewise first.");
    }
}
