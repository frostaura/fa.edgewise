using System.Text.Json;
using System.Text.Json.Nodes;
using Edgewise.Domain.Engines.Analytics;
using Edgewise.Domain.Engines.Calibration;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Coach;

/// <summary>
/// The read-only, user-scoped analytics tools exposed to the coach chat loop. Every tool
/// queries through the per-user-filtered DbContext, so the model can only ever see the
/// current user's rows.
/// </summary>
public sealed class CoachChatTools(EdgewiseDbContext db)
{
    private readonly EdgewiseDbContext _db = db;

    public static IReadOnlyList<LlmToolDefinition> Definitions { get; } = BuildDefinitions();

    private static LlmToolDefinition Tool(string name, string description, string schemaJson) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonNode.Parse(schemaJson)!,
    };

    private static IReadOnlyList<LlmToolDefinition> BuildDefinitions() =>
    [
        Tool("get_expectancy",
            "Expectancy (win rate, avg win/loss R, expectancy R, Wilson interval, low-sample flag) grouped by a dimension, over closed trades. Optional ISO date range.",
            """
            {"type":"object","properties":{
              "groupBy":{"type":"string","enum":["setup","instrument","emotion","dayOfWeek","hourOfDay"]},
              "from":{"type":"string","description":"ISO date, inclusive"},
              "to":{"type":"string","description":"ISO date, exclusive"}},
             "required":["groupBy"]}
            """),
        Tool("get_trades",
            "List the user's trades (newest first). Filters: status open|closed, setupTag, from/to ISO dates on close time, limit (default 20, max 50).",
            """
            {"type":"object","properties":{
              "status":{"type":"string","enum":["open","closed"]},
              "setupTag":{"type":"string"},
              "from":{"type":"string"},"to":{"type":"string"},
              "limit":{"type":"integer"}}}
            """),
        Tool("get_trade",
            "Full detail of one trade by id: fills, plan, adherence result, journal notes.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
        Tool("get_adherence_trend",
            "Average adherence score per ISO week over closed trades that have a score.",
            """{"type":"object","properties":{}}"""),
        Tool("get_r_distribution",
            "Histogram of realised R multiples over closed trades (0.5R bins).",
            """{"type":"object","properties":{}}"""),
        Tool("get_tilt",
            "Tilt signature: expectancy of trades entered within 60 minutes of a loss vs baseline, with significance.",
            """{"type":"object","properties":{}}"""),
        Tool("get_calibration",
            "Calibration report over resolved Brier forecasts: mean Brier vs market, reliability buckets, overconfidence index, longshot bias, monthly trend.",
            """{"type":"object","properties":{}}"""),
        Tool("get_ptr",
            "Plan-then-trade ratio per ISO week: how many closed trades had a plan before entry.",
            """{"type":"object","properties":{}}"""),
    ];

    /// <summary>Executes a tool call; returns a JSON string result (never throws for bad input).</summary>
    public async Task<string> ExecuteAsync(string name, string inputJson, CancellationToken ct)
    {
        JsonObject input;
        try
        {
            input = JsonNode.Parse(inputJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            input = [];
        }

        try
        {
            return name switch
            {
                "get_expectancy" => await GetExpectancyAsync(input, ct),
                "get_trades" => await GetTradesAsync(input, ct),
                "get_trade" => await GetTradeAsync(input, ct),
                "get_adherence_trend" => Json(RAnalyticsEngine.AdherenceTrend(await LoadStatsAsync(ct))),
                "get_r_distribution" => Json(RAnalyticsEngine.RDistribution(await LoadStatsAsync(ct))),
                "get_tilt" => Json(RAnalyticsEngine.TiltSignature(await LoadStatsAsync(ct))),
                "get_calibration" => await GetCalibrationAsync(ct),
                "get_ptr" => Json(RAnalyticsEngine.PtrTrend(await LoadStatsAsync(ct))),
                _ => """{"error":"unknown tool"}""",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, CoachJson.CamelCase);
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, CoachJson.CamelCase);

    private async Task<string> GetExpectancyAsync(JsonObject input, CancellationToken ct)
    {
        var stats = await LoadStatsAsync(ct);
        if (TryDate(input, "from", out var from))
        {
            stats = [.. stats.Where(s => s.ClosedAt >= from)];
        }

        if (TryDate(input, "to", out var to))
        {
            stats = [.. stats.Where(s => s.ClosedAt < to)];
        }

        var groupBy = input["groupBy"]?.GetValue<string>() ?? "setup";
        return groupBy switch
        {
            "instrument" => Json(RAnalyticsEngine.GroupedExpectancy(stats, s => s.InstrumentKey)),
            "emotion" => Json(RAnalyticsEngine.GroupedExpectancy(stats, s => s.EmotionTag)),
            "dayOfWeek" => Json(RAnalyticsEngine.GroupedExpectancy(stats, s => s.DayOfWeek.ToString())),
            "hourOfDay" => Json(RAnalyticsEngine.GroupedExpectancy(stats, s => s.HourOfDay)),
            _ => Json(RAnalyticsEngine.GroupedExpectancy(stats, s => s.SetupTag)),
        };
    }

    private async Task<string> GetTradesAsync(JsonObject input, CancellationToken ct)
    {
        var query = _db.Trades.AsQueryable();
        var status = input["status"]?.GetValue<string>();
        if (status == "open")
        {
            query = query.Where(t => t.Status == TradeStatus.Open);
        }
        else if (status == "closed")
        {
            query = query.Where(t => t.Status == TradeStatus.Closed);
        }

        if (TryDate(input, "from", out var from))
        {
            query = query.Where(t => t.ClosedAt >= from);
        }

        if (TryDate(input, "to", out var to))
        {
            query = query.Where(t => t.ClosedAt < to);
        }

        var limit = Math.Clamp(ReadInt(input["limit"], 20), 1, 50);
        var trades = await query.OrderByDescending(t => t.OpenedAt).Take(limit).ToListAsync(ct);

        var setupTag = input["setupTag"]?.GetValue<string>();
        var planIds = trades.Where(t => t.PlanId != null).Select(t => t.PlanId!.Value).ToList();
        var plans = await _db.TradePlans.Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var rows = trades
            .Select(t =>
            {
                plans.TryGetValue(t.PlanId ?? Guid.Empty, out var plan);
                return new
                {
                    id = t.Id,
                    citation = $"trade:{t.Id}",
                    t.Direction,
                    t.Status,
                    t.OpenedAt,
                    t.ClosedAt,
                    rRealised = t.RRealised,
                    pnlMinor = t.RealisedPnlMinor,
                    emotion = t.EmotionTag.ToString(),
                    setupTag = plan?.SetupTag,
                    hadPlan = t.PlanId != null,
                };
            })
            .Where(r => setupTag is null || string.Equals(r.setupTag, setupTag, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Json(rows);
    }

    private async Task<string> GetTradeAsync(JsonObject input, CancellationToken ct)
    {
        if (!Guid.TryParse(input["id"]?.GetValue<string>(), out var id))
        {
            return """{"error":"invalid trade id"}""";
        }

        var trade = await _db.Trades.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (trade is null)
        {
            return """{"error":"trade not found"}""";
        }

        var fills = await _db.Fills.Where(f => f.TradeId == id).OrderBy(f => f.At).ToListAsync(ct);
        var plan = trade.PlanId is Guid pid ? await _db.TradePlans.FirstOrDefaultAsync(p => p.Id == pid, ct) : null;
        var adherence = await _db.AdherenceResults
            .Where(a => a.TradeId == id)
            .OrderByDescending(a => a.ComputedAt)
            .FirstOrDefaultAsync(ct);
        var journal = await _db.JournalEntries.Where(j => j.TradeId == id).OrderBy(j => j.At).ToListAsync(ct);

        return Json(new
        {
            trade = new
            {
                trade.Id,
                citation = $"trade:{trade.Id}",
                trade.Direction,
                trade.Status,
                trade.OpenedAt,
                trade.ClosedAt,
                trade.Qty,
                trade.AvgEntryPrice,
                trade.AvgExitPrice,
                trade.RRealised,
                trade.RealisedPnlMinor,
                emotion = trade.EmotionTag.ToString(),
            },
            plan = plan is null ? null : new
            {
                plan.Id,
                plan.SetupTag,
                plan.TriggerText,
                plan.StopPrice,
                plan.SizeQty,
                plan.InvalidationNote,
            },
            adherence = adherence is null ? null : new { adherence.Score, adherence.Grade, adherence.DeductionsJson },
            journal = journal.Select(j => new { j.At, j.NotesMd }),
            fills = fills.Select(f => new { f.Side, f.Qty, f.Price, f.At }),
        });
    }

    private async Task<string> GetCalibrationAsync(CancellationToken ct)
    {
        var resolved = await _db.BrierForecasts
            .Where(f => f.Outcome != null && f.ResolvedAt != null)
            .ToListAsync(ct);
        if (resolved.Count == 0)
        {
            return """{"n":0,"note":"no resolved forecasts yet"}""";
        }

        var report = CalibrationEngine.BuildReport(
            [.. resolved.Select(f => new ForecastResolution(f.PUser, f.PMarket, f.Outcome!.Value, f.ResolvedAt!.Value))]);
        return Json(report);
    }

    private async Task<List<TradeStat>> LoadStatsAsync(CancellationToken ct)
    {
        var trades = await _db.Trades
            .Where(t => t.Status == TradeStatus.Closed && t.ClosedAt != null)
            .ToListAsync(ct);
        var tradeIds = trades.Select(t => t.Id).ToList();
        var adherence = await _db.AdherenceResults
            .Where(a => tradeIds.Contains(a.TradeId))
            .ToListAsync(ct);
        var planIds = trades.Where(t => t.PlanId != null).Select(t => t.PlanId!.Value).Distinct().ToList();
        var plans = await _db.TradePlans.Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var instruments = await _db.Instruments.ToDictionaryAsync(i => i.Id, i => i.Symbol, ct);
        var adherenceByTrade = adherence
            .GroupBy(a => a.TradeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.ComputedAt).First());

        return [.. trades.Select(t =>
        {
            plans.TryGetValue(t.PlanId ?? Guid.Empty, out var plan);
            adherenceByTrade.TryGetValue(t.Id, out var a);
            instruments.TryGetValue(t.InstrumentId, out var symbol);
            return new TradeStat
            {
                ClosedAt = t.ClosedAt!.Value,
                RRealised = t.RRealised,
                RPlanned = t.RPlanned,
                PnlMinor = t.RealisedPnlMinor,
                SetupTag = plan?.SetupTag ?? "(none)",
                InstrumentKey = symbol ?? t.InstrumentId.ToString(),
                EmotionTag = t.EmotionTag.ToString(),
                HadPlan = t.PlanId != null,
                AdherenceScore = a?.Score,
                HoldingSeconds = t.HoldingSeconds,
                IsWin = t.RealisedPnlMinor > 0,
                DayOfWeek = t.ClosedAt!.Value.DayOfWeek,
                HourOfDay = t.ClosedAt!.Value.Hour,
            };
        })];
    }

    private static bool TryDate(JsonObject input, string key, out DateTime value)
    {
        value = default;
        var raw = input[key]?.GetValue<string>();
        if (raw is null || !DateTime.TryParse(raw, CultureInfo(), System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static System.Globalization.CultureInfo CultureInfo() =>
        System.Globalization.CultureInfo.InvariantCulture;

    private static int ReadInt(JsonNode? node, int fallback)
    {
        try
        {
            return node?.GetValue<int>() ?? fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }
}
