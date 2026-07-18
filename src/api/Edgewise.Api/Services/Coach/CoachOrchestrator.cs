using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Edgewise.Domain.Engines.Adherence;
using Edgewise.Domain.Engines.Analytics;
using Edgewise.Domain.Engines.Calibration;
using Edgewise.Domain.Engines.Coach;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Llm;
using Edgewise.Infrastructure.Llm.Validation;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Coach;

/// <summary>
/// Builds coach insights. Every generator follows the same shape: assemble an id-tagged
/// context pack, try the LLM through the gateway (validated + one repair retry), and fall
/// back to an always-available deterministic insight (ModelTag "deterministic") when the
/// LLM is unavailable, over budget, opted out, or fails validation twice.
/// </summary>
public sealed class CoachOrchestrator(EdgewiseDbContext db, LlmGateway gateway)
{
    public const string DeterministicTag = "deterministic";

    private readonly EdgewiseDbContext _db = db;
    private readonly LlmGateway _gateway = gateway;

    private static readonly string SystemPrompt = $$"""
        You are the Edgewise trading coach. Hard rules:
        - NEVER advise, recommend or instruct any market action. No buy/sell/enter/exit/add/close
          instructions, ever. You review the trader's process; you do not make trade calls.
        - Grade the PROCESS, not the outcome. A rule-following loss is good process — praise
          losers done right. A rule-breaking win is still bad process.
        - Outcome-agnostic tone: specific, neutral, direct, kind.
        - At most 150 words of rendered text in total.
        - EVERY item must cite at least one context id exactly as given in square brackets
          (e.g. trade:..., plan:..., fill:..., adherence:..., bars:..., stat:...). Uncited
          claims are discarded by a validator.
        - Output ONLY one JSON document matching:
          {"observations":[{"text":"...","citations":["id"]}],"deviations":[...],
           "riskFlags":[...],"patternLinks":[...],
           "question":{"text":"...","citations":["id"]},"kudos":{"text":"...","citations":["id"]}}
          Sections may be omitted; include at least one item overall. No markdown, no prose.
        """;

    private Task<Guid> GetUserIdAsync(CancellationToken ct) =>
        _db.Users.Select(u => u.Id).SingleAsync(ct);

    // ==================================================== per-trade post-mortem

    public async Task<Insight> GeneratePostMortemAsync(Guid tradeId, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        var trade = await _db.Trades.FirstOrDefaultAsync(t => t.Id == tradeId, ct)
            ?? throw ApiException.NotFound("trade_not_found", "Trade not found.");

        var instrument = await _db.Instruments.FirstOrDefaultAsync(i => i.Id == trade.InstrumentId, ct);
        var symbol = instrument?.Symbol ?? "instrument";
        var plan = trade.PlanId is Guid planId
            ? await _db.TradePlans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            : null;
        var planVersions = plan is null
            ? 0
            : await _db.TradePlanVersions.CountAsync(v => v.PlanId == plan.Id, ct);
        var fills = await _db.Fills
            .Where(f => f.TradeId == trade.Id)
            .OrderBy(f => f.At)
            .Take(12)
            .ToListAsync(ct);
        var adherence = await _db.AdherenceResults
            .Where(a => a.TradeId == trade.Id)
            .OrderByDescending(a => a.ComputedAt)
            .FirstOrDefaultAsync(ct);
        var journal = await _db.JournalEntries
            .Where(j => j.TradeId == trade.Id)
            .OrderBy(j => j.At)
            .Take(5)
            .ToListAsync(ct);

        var pack = new ContextPack();
        var tradeRef = $"trade:{trade.Id}";
        pack.Add(tradeRef, $"{symbol} trade {trade.OpenedAt:dd MMM}",
            $"{trade.Direction} {symbol}, qty {trade.Qty:0.####} @ {trade.AvgEntryPrice:0.####}, " +
            $"exit {(trade.AvgExitPrice is decimal x ? x.ToString("0.####", CultureInfo.InvariantCulture) : "n/a")}, " +
            $"opened {trade.OpenedAt:u}, closed {trade.ClosedAt:u}, realised R {(trade.RRealised is decimal r ? r.ToString("0.##", CultureInfo.InvariantCulture) : "n/a")}, " +
            $"PnL {trade.RealisedPnlMinor / 100m:0.##} {trade.Currency}, emotion tag {trade.EmotionTag}.");

        if (plan is not null)
        {
            pack.Add($"plan:{plan.Id}", $"{symbol} plan",
                $"Plan created {plan.CreatedAt:u} ({planVersions} version(s)): setup '{plan.SetupTag}', " +
                $"trigger '{plan.TriggerText}', stop {plan.StopPrice:0.####}, target rule {plan.TargetRuleJson ?? "n/a"}, " +
                $"size {plan.SizeQty:0.####}{(plan.SizeOverridden ? " (size overridden)" : "")}, " +
                $"invalidation '{plan.InvalidationNote}'.");
        }
        else
        {
            pack.Add("stat:no_plan", "no plan", "No trade plan existed for this trade.");
        }

        foreach (var fill in fills)
        {
            pack.Add($"fill:{fill.Id}", $"{fill.Side} fill {fill.At:HH:mm}",
                $"{fill.Side} {fill.Qty:0.####} @ {fill.Price:0.####} at {fill.At:u}.");
        }

        List<AdherenceDeduction> deductions = [];
        if (adherence is not null)
        {
            deductions = ParseDeductions(adherence.DeductionsJson);
            pack.Add($"adherence:{adherence.Id}", $"adherence {adherence.Grade}",
                $"Adherence score {adherence.Score}/100, grade {adherence.Grade}" +
                (deductions.Count == 0
                    ? ", no deductions."
                    : $", deductions: {string.Join("; ", deductions.Select(d => $"{d.Code} (-{d.Points}: {d.Evidence})"))}."));
        }

        foreach (var entry in journal)
        {
            pack.Add(tradeRef, "journal note", $"Journal note ({entry.At:u}): {Truncate(entry.NotesMd, 300)}");
        }

        await AddBarsFactAsync(pack, trade, symbol, ct);
        await AddHistoryDigestAsync(pack, trade, deductions, ct);

        if (trade.RRealised is decimal rr)
        {
            pack.Add("stat:r_realised", "realised R", $"Realised R multiple: {rr:0.##}.");
        }

        var fallback = BuildDeterministicPostMortem(pack, trade, symbol, adherence, deductions);
        var (content, modelTag) = await TryLlmInsightAsync(
            userId, "postmortem", _gateway.Options.SmallModel, pack,
            $"Write the post-mortem for this closed trade. Context pack:\n{pack.RenderFacts()}",
            fallback, ct);

        return await StoreInsightAsync(
            userId, InsightType.PostMortem, trade.Id, content, pack, modelTag,
            supersede: await _db.Insights
                .Where(i => i.Type == InsightType.PostMortem && i.TradeId == trade.Id && i.SupersededById == null)
                .ToListAsync(ct),
            meta: null, ct);
    }

    private async Task AddBarsFactAsync(ContextPack pack, Trade trade, string symbol, CancellationToken ct)
    {
        var closedAt = trade.ClosedAt ?? trade.OpenedAt;
        var from = trade.OpenedAt.AddHours(-15);
        var to = closedAt.AddHours(15);
        var bars = await _db.PriceBars
            .Where(b => b.InstrumentId == trade.InstrumentId && b.Ts >= from && b.Ts <= to)
            .OrderBy(b => b.Ts)
            .Take(30)
            .ToListAsync(ct);
        if (bars.Count == 0)
        {
            return;
        }

        var range = $"{bars[0].Ts:yyyyMMddHHmm}-{bars[^1].Ts:yyyyMMddHHmm}";
        var lo = bars.Min(b => b.L);
        var hi = bars.Max(b => b.H);
        pack.Add($"bars:{Sanitize(symbol)}:{range}", $"{symbol} bars",
            $"{bars.Count} price bars around the trade: open {bars[0].O:0.####}, low {lo:0.####}, " +
            $"high {hi:0.####}, close {bars[^1].C:0.####}.");
    }

    private async Task AddHistoryDigestAsync(
        ContextPack pack, Trade trade, List<AdherenceDeduction> deductions, CancellationToken ct)
    {
        if (deductions.Count == 0)
        {
            return;
        }

        var topCode = deductions.OrderByDescending(d => d.Points).First().Code;
        var since = (trade.ClosedAt ?? DateTime.UtcNow).AddDays(-30);
        var recent = await _db.AdherenceResults
            .Where(a => a.ComputedAt >= since && a.DeductionsJson != null)
            .Select(a => a.DeductionsJson!)
            .ToListAsync(ct);
        var count = recent.Count(json => ParseDeductions(json).Any(d => d.Code == topCode));
        if (count >= 2)
        {
            pack.Add("stat:history_digest", "30-day pattern",
                $"{Ordinal(count)} {topCode} deduction in the last 30 days.");
        }
    }

    private InsightContent BuildDeterministicPostMortem(
        ContextPack pack, Trade trade, string symbol, AdherenceResult? adherence, List<AdherenceDeduction> deductions)
    {
        var tradeRef = $"trade:{trade.Id}";
        var observations = new List<InsightItem>();
        var deviations = new List<InsightItem>();
        var patternLinks = new List<InsightItem>();
        InsightItem? question = null;
        InsightItem? kudos = null;

        var rText = trade.RRealised is decimal r ? $"{r:0.##}R" : "an unmeasured R";
        var summaryCites = new List<string> { tradeRef };
        if (adherence is not null)
        {
            summaryCites.Add($"adherence:{adherence.Id}");
        }

        observations.Add(new InsightItem(
            adherence is null
                ? $"This {trade.Direction} {symbol} trade closed at {rText}; no adherence score was computed."
                : $"This {trade.Direction} {symbol} trade closed at {rText} with adherence {adherence.Grade} ({adherence.Score}/100).",
            summaryCites));

        foreach (var d in deductions.Take(4))
        {
            deviations.Add(new InsightItem(
                $"{FriendlyDeduction(d.Code)} (-{d.Points}): {d.Evidence}",
                [$"adherence:{adherence!.Id}"]));
        }

        if (pack.ValidIds.Contains("stat:history_digest"))
        {
            patternLinks.Add(new InsightItem(
                "The same deviation has recurred across the last 30 days.",
                ["stat:history_digest"]));
        }

        var isLoss = trade.RealisedPnlMinor < 0;
        if (adherence is not null && deductions.Count == 0)
        {
            kudos = new InsightItem(
                isLoss
                    ? "A rule-following loss: the plan, size and exit all matched the rules. That is good process worth repeating."
                    : "Clean execution: plan, size and exit all matched the rules.",
                [$"adherence:{adherence.Id}"]);
        }

        if (deductions.Count > 0)
        {
            var top = deductions.OrderByDescending(d => d.Points).First();
            question = new InsightItem(
                QuestionFor(top.Code),
                [$"adherence:{adherence!.Id}"]);
        }
        else if (pack.ValidIds.Contains("stat:no_plan"))
        {
            deviations.Add(new InsightItem(
                "The position was opened without an active plan, so process quality could not be graded against a commitment.",
                ["stat:no_plan"]));
            question = new InsightItem("What stopped a plan from existing before the first fill?", ["stat:no_plan"]);
        }

        return new InsightContent
        {
            Observations = observations,
            Deviations = deviations,
            PatternLinks = patternLinks,
            Question = question,
            Kudos = kudos,
        };
    }

    // ========================================================== weekly pack

    public async Task<(Insight Pack, WeeklyReview Review)> GenerateWeeklyPackAsync(
        DateOnly weekStart, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        var from = weekStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = from.AddDays(7);

        var trades = await _db.Trades
            .Where(t => t.Status == TradeStatus.Closed && t.ClosedAt >= from && t.ClosedAt < to)
            .ToListAsync(ct);
        var tradeIds = trades.Select(t => t.Id).ToList();
        var adherences = await _db.AdherenceResults
            .Where(a => tradeIds.Contains(a.TradeId))
            .ToListAsync(ct);
        var plans = await LoadPlansAsync(trades, ct);

        var pack = new ContextPack();
        var n = trades.Count;
        var sumR = trades.Sum(t => t.RRealised ?? 0m);
        var wins = trades.Count(t => t.RealisedPnlMinor > 0);
        pack.Add("stat:week_summary", $"week of {weekStart:dd MMM}",
            $"Week {weekStart:yyyy-MM-dd}: {n} closed trade(s), net {sumR:0.##}R, " +
            $"{(n == 0 ? 0 : 100m * wins / n):0}% winners.");

        if (adherences.Count > 0)
        {
            var avg = adherences.Average(a => (decimal)a.Score);
            var prevFrom = from.AddDays(-7);
            var prevIds = await _db.Trades
                .Where(t => t.Status == TradeStatus.Closed && t.ClosedAt >= prevFrom && t.ClosedAt < from)
                .Select(t => t.Id)
                .ToListAsync(ct);
            var prevAvg = await _db.AdherenceResults
                .Where(a => prevIds.Contains(a.TradeId))
                .Select(a => (decimal?)a.Score)
                .AverageAsync(ct);
            pack.Add("stat:adherence_trend", "adherence trend",
                $"Average adherence {avg:0} this week" +
                (prevAvg is decimal p ? $" vs {p:0} the week before." : "."));
        }

        var bySetup = RAnalyticsEngine.GroupedExpectancy(
            ToTradeStats(trades, adherences, plans), t => t.SetupTag);
        foreach (var g in bySetup.Groups.Where(g => g.N > 0).Take(4))
        {
            pack.Add($"stat:expectancy_{Sanitize(g.Key)}", $"expectancy {g.Key}",
                $"Setup '{g.Key}': expectancy {g.ExpectancyR:0.##}R over {g.N} trade(s)" +
                (g.LowSample ? " (low sample — treat with caution)." : "."));
        }

        var topDeviation = adherences
            .SelectMany(a => ParseDeductions(a.DeductionsJson))
            .GroupBy(d => d.Code)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        string? focus = null;
        if (topDeviation is not null)
        {
            pack.Add("stat:top_deviation", "top deviation",
                $"Most recurring deviation: {topDeviation.Key} ({topDeviation.Count()}×).");
            focus = $"Reduce {FriendlyDeduction(topDeviation.Key).ToLowerInvariant()} — one deliberate change this week.";
            pack.Add("stat:suggested_focus", "suggested focus", $"Suggested single focus: {focus}");
        }

        var fallback = BuildDeterministicWeeklyPack(pack, n, sumR, topDeviation?.Key);
        var (content, modelTag) = await TryLlmInsightAsync(
            userId, "weekly_pack", _gateway.Options.LargeModel, pack,
            $"Write the weekly review pack for the week starting {weekStart:yyyy-MM-dd}. Context pack:\n{pack.RenderFacts()}",
            fallback, ct);

        var review = await _db.WeeklyReviews.FirstOrDefaultAsync(w => w.WeekStartDate == weekStart, ct);
        var supersede = review?.PackInsightId is Guid oldPack
            ? await _db.Insights.Where(i => i.Id == oldPack && i.SupersededById == null).ToListAsync(ct)
            : [];

        var insight = await StoreInsightAsync(
            userId, InsightType.WeeklyPack, null, content, pack, modelTag, supersede,
            meta: new Dictionary<string, string?>
            {
                ["weekStart"] = weekStart.ToString("yyyy-MM-dd"),
                ["suggestedFocus"] = focus,
            }, ct);

        if (review is null)
        {
            review = new WeeklyReview
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                WeekStartDate = weekStart,
                PackInsightId = insight.Id,
            };
            _db.WeeklyReviews.Add(review);
        }
        else
        {
            review.PackInsightId = insight.Id;
        }

        await _db.SaveChangesAsync(ct);
        return (insight, review);
    }

    private static InsightContent BuildDeterministicWeeklyPack(
        ContextPack pack, int n, decimal sumR, string? topDeviationCode)
    {
        var observations = new List<InsightItem>
        {
            new(n == 0
                    ? "No trades closed this week — a quiet week is a data point too."
                    : $"{n} trade(s) closed for a net {sumR:0.##}R.",
                ["stat:week_summary"]),
        };
        if (pack.ValidIds.Contains("stat:adherence_trend"))
        {
            observations.Add(new InsightItem("Adherence held its weekly trend.", ["stat:adherence_trend"]));
        }

        var deviations = new List<InsightItem>();
        var patternLinks = pack.ValidIds
            .Where(id => id.StartsWith("stat:expectancy_", StringComparison.Ordinal))
            .Take(2)
            .Select(id => new InsightItem("Expectancy by setup is tracked for this week (small samples flagged).", [id]))
            .ToList();
        InsightItem? question = null;
        if (topDeviationCode is not null)
        {
            deviations.Add(new InsightItem(
                $"The most recurring deviation this week was {FriendlyDeduction(topDeviationCode).ToLowerInvariant()}.",
                ["stat:top_deviation"]));
            question = new InsightItem(
                "Which single rule, if followed every time next week, would remove the top deviation?",
                ["stat:suggested_focus"]);
        }

        return new InsightContent
        {
            Observations = observations,
            Deviations = deviations,
            PatternLinks = patternLinks,
            Question = question,
        };
    }

    // ============================================================ bias cards

    public async Task<List<Insight>> RefreshBiasCardsAsync(CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        var since = DateTime.UtcNow.AddDays(-90);
        var trades = await _db.Trades
            .Where(t => t.Status == TradeStatus.Closed && t.ClosedAt >= since)
            .ToListAsync(ct);
        if (trades.Count == 0)
        {
            return [];
        }

        var plans = await LoadPlansAsync(trades, ct);
        var biasTrades = trades
            .Where(t => t.ClosedAt is not null)
            .Select(t =>
            {
                plans.TryGetValue(t.PlanId ?? Guid.Empty, out var plan);
                decimal? risk = plan is null ? null : Math.Abs(t.AvgEntryPrice - plan.StopPrice) * t.Qty;
                return new BiasTrade
                {
                    Key = $"trade:{t.Id}",
                    EntryAt = t.OpenedAt,
                    ClosedAt = t.ClosedAt!.Value,
                    IsWin = t.RealisedPnlMinor > 0,
                    RRealised = t.RRealised,
                    HoldingSeconds = t.HoldingSeconds,
                    RiskAmount = risk,
                };
            })
            .ToList();

        var now = DateTime.UtcNow;
        var cards = new[]
        {
            BiasDetectors.DispositionRatio(biasTrades),
            BiasDetectors.RevengeEntries(biasTrades, now),
            BiasDetectors.SizeCreep(biasTrades),
        };

        var existing = await _db.Insights
            .Where(i => i.Type == InsightType.Bias && i.SupersededById == null)
            .ToListAsync(ct);

        var created = new List<Insight>();
        foreach (var card in cards.Where(c => c.Breached))
        {
            var pack = new ContextPack();
            var statRef = $"stat:bias_{card.Code.ToLowerInvariant()}";
            pack.Add(statRef, $"bias {card.Code}",
                $"Bias {card.Code}: metric {card.Metric:0.##} vs threshold {card.Threshold:0.##} (breached). " +
                $"Details: {string.Join(", ", card.Details.Select(d => $"{d.Key}={d.Value:0.##}"))}.");
            foreach (var key in card.EvidenceKeys.Take(5))
            {
                pack.Add(key, "evidence trade", $"Evidence trade {key}.");
            }

            var sentence = DeterministicBiasSentence(card);
            var fallback = new InsightContent
            {
                RiskFlags =
                [
                    new InsightItem(sentence, [statRef, .. card.EvidenceKeys.Take(5)]),
                ],
            };

            var (content, modelTag) = await TryLlmInsightAsync(
                userId, "bias_card", _gateway.Options.SmallModel, pack,
                $"Narrate this detected trading bias as one or two riskFlags items (no advice). Context pack:\n{pack.RenderFacts()}",
                fallback, ct);

            var supersede = existing.Where(i => ReadMeta(i.ContentJson, "code") == card.Code).ToList();
            created.Add(await StoreInsightAsync(
                userId, InsightType.Bias, null, content, pack, modelTag, supersede,
                meta: new Dictionary<string, string?> { ["code"] = card.Code }, ct));
        }

        return created;
    }

    private static string DeterministicBiasSentence(BiasCard card) => card.Code switch
    {
        BiasDetectors.DispositionCode =>
            $"Winners are held {card.Metric:0.0}× longer than losers (threshold {card.Threshold:0.0}×) — a disposition-effect signature.",
        BiasDetectors.RevengeCode =>
            $"{card.Metric:0} entr{(card.Metric == 1 ? "y" : "ies")} in the last 30 days came within 30 minutes of a prior loss.",
        BiasDetectors.SizeCreepCode =>
            $"Average risk taken right after a 3-win streak runs {card.Metric:0.0}× the baseline (threshold {card.Threshold:0.0}×).",
        BiasDetectors.FomoChaseCode =>
            $"{card.Metric:0} entries chased beyond the trigger price in the last 30 days.",
        _ => $"Bias {card.Code} breached: {card.Metric:0.##} vs threshold {card.Threshold:0.##}.",
    };

    // =============================================================== dossier

    public async Task<Insight> GenerateDossierAsync(Guid instrumentId, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        var instrument = await _db.Instruments.FirstOrDefaultAsync(i => i.Id == instrumentId, ct)
            ?? throw ApiException.NotFound("instrument_not_found", "Instrument not found.");

        var trades = await _db.Trades
            .Where(t => t.InstrumentId == instrumentId)
            .OrderByDescending(t => t.OpenedAt)
            .Take(50)
            .ToListAsync(ct);

        var pack = new ContextPack();
        var closed = trades.Where(t => t.Status == TradeStatus.Closed).ToList();
        pack.Add("stat:dossier_positions", $"{instrument.Symbol} history",
            $"Position history on {instrument.Symbol}: {trades.Count} trade(s), {closed.Count} closed, " +
            $"net {closed.Sum(t => t.RRealised ?? 0m):0.##}R, " +
            $"{(closed.Count == 0 ? 0 : 100m * closed.Count(t => t.RealisedPnlMinor > 0) / closed.Count):0}% winners" +
            (trades.Count > 0 ? $", last activity {trades[0].OpenedAt:u}." : "."));

        var tradeIds = trades.Select(t => t.Id).ToList();
        var notes = await _db.JournalEntries
            .Where(j => tradeIds.Contains(j.TradeId))
            .OrderByDescending(j => j.At)
            .Take(3)
            .ToListAsync(ct);
        foreach (var note in notes)
        {
            pack.Add($"trade:{note.TradeId}", "your note",
                $"USER NOTE (opinion, not fact) from {note.At:u}: {Truncate(note.NotesMd, 240)}");
        }

        var idNeedle = instrumentId.ToString();
        var news = (await _db.NewsItems
                .OrderByDescending(x => x.PublishedAt)
                .Take(100)
                .ToListAsync(ct))
            .Where(x => x.InstrumentIdsJson is not null && x.InstrumentIdsJson.Contains(idNeedle, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();
        for (var i = 0; i < news.Count; i++)
        {
            pack.Add($"stat:news_{i + 1}", $"{news[i].Title} ({news[i].Url})",
                $"NEWS FACT: \"{news[i].Title}\" ({news[i].Source}, {news[i].PublishedAt:dd MMM}).");
        }

        var catalysts = await _db.CatalystEvents
            .Where(c => c.InstrumentId == instrumentId && c.At >= DateTime.UtcNow)
            .OrderBy(c => c.At)
            .Take(5)
            .ToListAsync(ct);
        for (var i = 0; i < catalysts.Count; i++)
        {
            pack.Add($"stat:catalyst_{i + 1}", $"{catalysts[i].Title} {catalysts[i].At:dd MMM}",
                $"UPCOMING CATALYST: {catalysts[i].Kind} \"{catalysts[i].Title}\" at {catalysts[i].At:u} ({catalysts[i].Severity}).");
        }

        var fallback = BuildDeterministicDossier(pack, notes, news.Count, catalysts);
        var (content, modelTag) = await TryLlmInsightAsync(
            userId, "dossier", _gateway.Options.LargeModel, pack,
            $"Write an instrument dossier for {instrument.Symbol}. Keep verifiable facts (observations/riskFlags) " +
            $"clearly separate from the user's own notes (patternLinks). Context pack:\n{pack.RenderFacts()}",
            fallback, ct);

        var supersede = (await _db.Insights
                .Where(i => i.Type == InsightType.Dossier && i.SupersededById == null)
                .ToListAsync(ct))
            .Where(i => ReadMeta(i.ContentJson, "instrumentId") == instrumentId.ToString())
            .ToList();

        return await StoreInsightAsync(
            userId, InsightType.Dossier, null, content, pack, modelTag, supersede,
            meta: new Dictionary<string, string?> { ["instrumentId"] = instrumentId.ToString() }, ct);
    }

    private static InsightContent BuildDeterministicDossier(
        ContextPack pack, List<JournalEntry> notes, int newsCount, List<CatalystEvent> catalysts)
    {
        var observations = new List<InsightItem>
        {
            new("Position history summary for this instrument.", ["stat:dossier_positions"]),
        };
        for (var i = 1; i <= newsCount; i++)
        {
            observations.Add(new InsightItem("Recent headline on this instrument.", [$"stat:news_{i}"]));
        }

        var riskFlags = catalysts
            .Select((c, i) => new InsightItem(
                $"Upcoming {c.Kind} catalyst on {c.At:dd MMM} ({c.Severity}).", [$"stat:catalyst_{i + 1}"]))
            .ToList();

        var patternLinks = notes
            .Select(n2 => new InsightItem(
                $"[Your note] {Truncate(n2.NotesMd, 120)}", [$"trade:{n2.TradeId}"]))
            .ToList();

        return new InsightContent
        {
            Observations = observations,
            RiskFlags = riskFlags,
            PatternLinks = patternLinks,
        };
    }

    // =========================================================== calibration

    /// <summary>Deterministic calibration summary insight (Type Calibration), superseding the previous one.</summary>
    public async Task<Insight> GenerateCalibrationInsightAsync(CalibrationReport report, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        var pack = new ContextPack();
        pack.Add("stat:calibration", "calibration report",
            $"Calibration over {report.N} resolved forecast(s): mean Brier {report.MeanBrier:0.###} vs market {report.MeanMarketBrier:0.###}.");
        pack.Add("stat:overconfidence", "overconfidence index",
            $"Overconfidence index {report.OverconfidenceIndex:+0.###;-0.###;0}.");
        if (report.LongshotBias is decimal lb)
        {
            pack.Add("stat:longshot_bias", "longshot bias", $"Longshot bias {lb:+0.###;-0.###;0}.");
        }

        var edge = report.MeanMarketBrier - report.MeanBrier;
        var content = new InsightContent
        {
            Observations =
            [
                new InsightItem(
                    $"Mean Brier {report.MeanBrier:0.###} vs market {report.MeanMarketBrier:0.###} over {report.N} forecasts — " +
                    (edge >= 0 ? $"you beat the market by {edge:0.###}." : $"the market beats you by {-edge:0.###}."),
                    ["stat:calibration"]),
                new InsightItem(
                    report.OverconfidenceIndex > 0.02m
                        ? $"You run overconfident: stated probabilities exceed realised frequency by {report.OverconfidenceIndex:0.###}."
                        : report.OverconfidenceIndex < -0.02m
                            ? $"You run underconfident by {-report.OverconfidenceIndex:0.###}."
                            : "Stated probabilities track realised frequencies closely.",
                    ["stat:overconfidence"]),
            ],
            RiskFlags = report.LongshotBias is decimal bias && Math.Abs(bias) > 0.05m
                ?
                [
                    new InsightItem(
                        bias > 0
                            ? "Longshots resolve true more often than you predict — your sub-15% calls are undercooked."
                            : "Longshots resolve true less often than you predict.",
                        ["stat:longshot_bias"]),
                ]
                : [],
        };

        var supersede = await _db.Insights
            .Where(i => i.Type == InsightType.Calibration && i.SupersededById == null)
            .ToListAsync(ct);
        return await StoreInsightAsync(
            userId, InsightType.Calibration, null, content, pack, DeterministicTag, supersede, null, ct);
    }

    // =========================================================== shared bits

    /// <summary>LLM attempt with validator + one repair retry; deterministic fallback otherwise.</summary>
    private async Task<(InsightContent Content, string ModelTag)> TryLlmInsightAsync(
        Guid userId,
        string purpose,
        string model,
        ContextPack pack,
        string userPrompt,
        InsightContent fallback,
        CancellationToken ct)
    {
        var validIds = pack.ValidIds;
        var messages = new List<LlmMessage> { LlmMessage.User(userPrompt) };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await _gateway.TryCompleteAsync(userId, purpose, new LlmRequest
            {
                Model = model,
                System = SystemPrompt,
                Messages = messages,
                MaxTokens = 1024,
                JsonMode = true,
            }, ct);

            if (result is null)
            {
                break; // offline / opted out / over budget / provider failure
            }

            var validation = InsightSchema.Validate(StripFences(result.Text), validIds);
            if (validation.Success)
            {
                return (validation.Content!, result.Model.Length > 0 ? result.Model : model);
            }

            messages.Add(LlmMessage.Assistant(result.Text));
            messages.Add(LlmMessage.User(
                "Your reply failed validation:\n- " + string.Join("\n- ", validation.Errors) +
                "\nFix these problems and output ONLY the corrected JSON document."));
        }

        // Deterministic fallback is sanitised through the same pure pipeline for consistency.
        var sanitized = InsightValidator.Sanitize(fallback, validIds);
        return (sanitized.Content ?? fallback, DeterministicTag);
    }

    private async Task<Insight> StoreInsightAsync(
        Guid userId,
        InsightType type,
        Guid? tradeId,
        InsightContent content,
        ContextPack pack,
        string modelTag,
        List<Insight> supersede,
        Dictionary<string, string?>? meta,
        CancellationToken ct)
    {
        var insight = new Insight
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TradeId = tradeId,
            Type = type,
            ContentJson = CoachJson.SerializeContent(content, meta),
            CitationsJson = CoachJson.SerializeCitations(pack.ResolveCitations(content)),
            ModelTag = modelTag,
            PromptVersion = InsightSchema.PromptVersion,
            Feedback = InsightFeedback.None,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Insights.Add(insight);
        foreach (var old in supersede)
        {
            old.SupersededById = insight.Id;
        }

        await _db.SaveChangesAsync(ct);
        return insight;
    }

    private async Task<Dictionary<Guid, TradePlan>> LoadPlansAsync(List<Trade> trades, CancellationToken ct)
    {
        var planIds = trades.Where(t => t.PlanId is not null).Select(t => t.PlanId!.Value).Distinct().ToList();
        return await _db.TradePlans.Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
    }

    private static List<TradeStat> ToTradeStats(
        List<Trade> trades, List<AdherenceResult> adherences, Dictionary<Guid, TradePlan> plans)
    {
        var adherenceByTrade = adherences
            .GroupBy(a => a.TradeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.ComputedAt).First());
        return [.. trades
            .Where(t => t.ClosedAt is not null)
            .Select(t =>
            {
                plans.TryGetValue(t.PlanId ?? Guid.Empty, out var plan);
                adherenceByTrade.TryGetValue(t.Id, out var adherence);
                return new TradeStat
                {
                    ClosedAt = t.ClosedAt!.Value,
                    RRealised = t.RRealised,
                    RPlanned = t.RPlanned,
                    PnlMinor = t.RealisedPnlMinor,
                    SetupTag = plan?.SetupTag ?? "(none)",
                    InstrumentKey = t.InstrumentId.ToString(),
                    EmotionTag = t.EmotionTag.ToString(),
                    HadPlan = t.PlanId is not null,
                    AdherenceScore = adherence?.Score,
                    HoldingSeconds = t.HoldingSeconds,
                    IsWin = t.RealisedPnlMinor > 0,
                    DayOfWeek = t.ClosedAt!.Value.DayOfWeek,
                    HourOfDay = t.ClosedAt!.Value.Hour,
                };
            })];
    }

    private static List<AdherenceDeduction> ParseDeductions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var node = JsonNode.Parse(json);
            var array = node as JsonArray ?? node?["deductions"] as JsonArray;
            if (array is null)
            {
                return [];
            }

            return [.. array
                .OfType<JsonObject>()
                .Select(o => new AdherenceDeduction(
                    o["code"]?.GetValue<string>() ?? o["Code"]?.GetValue<string>() ?? "UNKNOWN",
                    ReadInt(o["points"] ?? o["Points"]),
                    o["evidence"]?.GetValue<string>() ?? o["Evidence"]?.GetValue<string>() ?? string.Empty))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int ReadInt(JsonNode? node)
    {
        try
        {
            return node?.GetValue<int>() ?? 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return 0;
        }
    }

    internal static string? ReadMeta(string contentJson, string key)
    {
        try
        {
            return JsonNode.Parse(contentJson)?[key]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = t.IndexOf('\n');
            if (firstNewline >= 0)
            {
                t = t[(firstNewline + 1)..];
            }

            var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
            {
                t = t[..lastFence];
            }
        }

        return t.Trim();
    }

    private static string Sanitize(object? key)
    {
        var s = key?.ToString() ?? "none";
        return new string([.. s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_')]);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{n}th",
    };

    private static string FriendlyDeduction(string code) => code.Replace('_', ' ') switch
    {
        var friendly => char.ToUpperInvariant(friendly[0]) + friendly[1..].ToLowerInvariant(),
    };

    private static string QuestionFor(string code) => code.ToUpperInvariant() switch
    {
        "EARLY_EXIT" => "What did you feel in the moment you exited ahead of the rule?",
        "SIZE_EXCEEDED" => "What made the planned size feel insufficient at entry time?",
        "STOP_WIDENED" => "What story were you telling yourself when the stop moved?",
        "NO_PLAN" => "What stopped a plan from existing before the first fill?",
        "NO_TRIGGER" => "What was the actual trigger you acted on, if not the planned one?",
        "REVENGE_WINDOW" => "What would a 30-minute cooling-off rule have changed here?",
        "RED_EVENT" => "Was holding through the red event a decision or a default?",
        "HEAT_CAP" => "What made the extra open risk feel acceptable at entry?",
        "CB_OVERRIDE" => "What did overriding the circuit breaker buy you, in hindsight?",
        _ => "If you replayed this trade, which single decision would you change?",
    };
}
