using System.Globalization;

namespace Edgewise.Domain.Engines.Adherence;

/// <summary>
/// Pure scoring engine implementing the Adherence Rubric v1.
/// Starts at 100, applies fixed deductions, floors at 0. Unplanned trades are capped at 60.
/// </summary>
public static class AdherenceEngine
{
    public const string RubricVersion = "v1";

    /// <summary>Multiplicative tolerance applied to the risk budget before SIZE_EXCEEDED fires.</summary>
    public const decimal SizeTolerance = 1.05m;

    /// <summary>Trades entered fewer than this many minutes after a same-class loss are flagged.</summary>
    public const int RevengeWindowMinutes = 30;

    /// <summary>Maximum score achievable by an unplanned trade.</summary>
    public const int UnplannedScoreCap = 60;

    public static class Codes
    {
        public const string NoPlan = "NO_PLAN";
        public const string SizeExceeded = "SIZE_EXCEEDED";
        public const string StopWidened = "STOP_WIDENED";
        public const string NoTrigger = "NO_TRIGGER";
        public const string EarlyExit = "EARLY_EXIT";
        public const string RevengeWindow = "REVENGE_WINDOW";
        public const string RedEvent = "RED_EVENT";
        public const string CbOverride = "CB_OVERRIDE";
        public const string HeatCap = "HEAT_CAP";
    }

    /// <summary>The rubric expressed as data, in evaluation order, so the API can serve it.</summary>
    public static readonly IReadOnlyList<RubricRule> Rubric =
    [
        new(Codes.NoPlan, 40, "No plan existed before entry (plan missing or created at/after first fill). Final score capped at 60.", false),
        new(Codes.SizeExceeded, 20, "Actual position risk (max qty x entry-to-stop distance) exceeded bucket risk budget by more than 5% tolerance.", false),
        new(Codes.StopWidened, 25, "Stop was moved further from entry after the first fill (direction-aware).", false),
        new(Codes.NoTrigger, 15, "Entry-trigger checklist was not confirmed.", true),
        new(Codes.EarlyExit, 10, "Exit was discretionary and earlier than the planned exit rule.", false),
        new(Codes.RevengeWindow, 20, "Entered fewer than 30 minutes after a loss in the same instrument class.", false),
        new(Codes.RedEvent, 15, "Traded through a flagged red event.", false),
        new(Codes.CbOverride, 25, "Circuit breaker was overridden.", false),
        new(Codes.HeatCap, 15, "Open risk at entry exceeded the portfolio heat cap.", false),
    ];

    /// <summary>Maps an integer score to a letter grade: A&gt;=90, B&gt;=75, C&gt;=60, D&gt;=40, F otherwise.</summary>
    public static string GradeFor(int score) =>
        score >= 90 ? "A" :
        score >= 75 ? "B" :
        score >= 60 ? "C" :
        score >= 40 ? "D" : "F";

    /// <summary>Scores a trade against Adherence Rubric v1.</summary>
    public static AdherenceScore Score(AdherenceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var deductions = new List<AdherenceDeduction>();

        // NO_PLAN: plan must exist and pre-date the first fill.
        var unplanned = input.PlanCreatedAt is null || input.PlanCreatedAt.Value >= input.FirstFillAt;
        if (unplanned)
        {
            var evidence = input.PlanCreatedAt is null
                ? "No plan attached to trade."
                : $"Plan created at {Iso(input.PlanCreatedAt.Value)}, not before first fill at {Iso(input.FirstFillAt)}.";
            Deduct(deductions, Codes.NoPlan, evidence);
        }

        // SIZE_EXCEEDED: actual position risk vs bucket risk budget with 5% tolerance.
        var avgEntry = AverageEntryPrice(input.EntryFills);
        if (avgEntry is decimal entry)
        {
            var actualRisk = input.ActualMaxPositionQty * Math.Abs(entry - input.PlannedStopPrice);
            var allowedRisk = input.BucketEquityMinor * input.RiskPct * SizeTolerance;
            if (actualRisk > allowedRisk)
            {
                Deduct(deductions, Codes.SizeExceeded,
                    $"Actual risk {Dec(actualRisk)} (qty {Dec(input.ActualMaxPositionQty)} x distance {Dec(Math.Abs(entry - input.PlannedStopPrice))}) exceeded allowed {Dec(allowedRisk)} (equity {input.BucketEquityMinor} x risk {Dec(input.RiskPct)} x {Dec(SizeTolerance)}).");
            }
        }

        // STOP_WIDENED: any post-entry stop version further from entry than the version active at entry.
        var baselineStop = ActiveStopAtEntry(input);
        foreach (var version in input.StopVersions)
        {
            if (version.At <= input.FirstFillAt)
            {
                continue;
            }

            var widened = input.Direction == TradeDirection.Long
                ? version.Stop < baselineStop
                : version.Stop > baselineStop;
            if (widened)
            {
                Deduct(deductions, Codes.StopWidened,
                    $"Stop moved from {Dec(baselineStop)} to {Dec(version.Stop)} at {Iso(version.At)} ({input.Direction}), further from entry.");
                break;
            }
        }

        // NO_TRIGGER: checklist not confirmed (self-report).
        if (!input.TriggerChecklistConfirmed)
        {
            Deduct(deductions, Codes.NoTrigger, "Trigger checklist not confirmed (self-report).");
        }

        // EARLY_EXIT: discretionary exit ahead of the planned rule.
        if (input.ExitRule == ExitRuleOutcome.EarlyDiscretionary)
        {
            Deduct(deductions, Codes.EarlyExit, "Exit classified as early discretionary rather than matching the planned exit rule.");
        }

        // REVENGE_WINDOW: entered too soon after a same-class loss.
        if (input.MinutesSincePriorLossSameClass is int minutes && minutes < RevengeWindowMinutes)
        {
            Deduct(deductions, Codes.RevengeWindow,
                $"Entered {minutes} minutes after prior same-class loss (window {RevengeWindowMinutes} minutes).");
        }

        // RED_EVENT.
        if (input.TradedThroughRedEvent)
        {
            Deduct(deductions, Codes.RedEvent, "Traded through a flagged red event.");
        }

        // CB_OVERRIDE.
        if (input.CircuitBreakerOverride)
        {
            Deduct(deductions, Codes.CbOverride, "Circuit breaker was active and overridden.");
        }

        // HEAT_CAP: open risk at entry above the heat cap.
        if (input.OpenRiskAtEntryPct > input.HeatCapPct)
        {
            Deduct(deductions, Codes.HeatCap,
                $"Open risk at entry {Dec(input.OpenRiskAtEntryPct)} exceeded heat cap {Dec(input.HeatCapPct)}.");
        }

        var score = 100 - deductions.Sum(d => d.Points);
        if (unplanned)
        {
            score = Math.Min(score, UnplannedScoreCap);
        }

        score = Math.Max(0, score);
        return new AdherenceScore(score, GradeFor(score), deductions, RubricVersion);
    }

    private static void Deduct(List<AdherenceDeduction> deductions, string code, string evidence)
    {
        var rule = Rubric.First(r => r.Code == code);
        deductions.Add(new AdherenceDeduction(rule.Code, rule.Points, evidence));
    }

    private static decimal? AverageEntryPrice(IReadOnlyList<TradeFill> entryFills)
    {
        var totalQty = entryFills.Sum(f => f.Qty);
        if (totalQty <= 0)
        {
            return null;
        }

        return entryFills.Sum(f => f.Qty * f.Price) / totalQty;
    }

    private static decimal ActiveStopAtEntry(AdherenceInput input)
    {
        StopVersion? active = null;
        foreach (var version in input.StopVersions)
        {
            if (version.At <= input.FirstFillAt && (active is null || version.At >= active.Value.At))
            {
                active = version;
            }
        }

        return active?.Stop ?? input.PlannedStopPrice;
    }

    private static string Iso(DateTime at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static string Dec(decimal value)
    {
        // Trim trailing decimal zeros so evidence strings stay readable
        // (values arrive with full decimal(28,10) precision).
        var normalized = value / 1.000000000000000000000000000000000m;
        return normalized.ToString(CultureInfo.InvariantCulture);
    }
}
