using Edgewise.Domain.Engines.Adherence;

namespace Edgewise.Domain.Tests.Adherence;

internal static class TestInputs
{
    public static readonly DateTime Fill = new(2026, 7, 1, 14, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// A fully compliant trade: plan pre-dates entry, size within budget, stop untouched,
    /// checklist confirmed, rule-matched exit, no revenge/red-event/CB/heat issues.
    /// Risk: 10 qty x |10000 - 9500| = 5000 vs allowed 1,000,000 x 0.01 x 1.05 = 10,500.
    /// </summary>
    public static AdherenceInput Clean() => new()
    {
        PlanCreatedAt = Fill.AddHours(-2),
        FirstFillAt = Fill,
        Direction = TradeDirection.Long,
        StopVersions = [new StopVersion(9_500m, Fill.AddHours(-2))],
        EntryFills = [new TradeFill(10m, 10_000m, Fill)],
        ExitFills = [new TradeFill(10m, 10_800m, Fill.AddHours(1))],
        PlannedStopPrice = 9_500m,
        PlanSizeQty = 10m,
        ActualMaxPositionQty = 10m,
        BucketEquityMinor = 1_000_000,
        RiskPct = 0.01m,
        HeatCapPct = 0.05m,
        OpenRiskAtEntryPct = 0.02m,
        TriggerChecklistConfirmed = true,
        ExitRule = ExitRuleOutcome.MatchedRule,
        MinutesSincePriorLossSameClass = null,
        TradedThroughRedEvent = false,
        CircuitBreakerOverride = false,
    };

    public static IReadOnlyList<string> Codes(AdherenceScore score) =>
        score.Deductions.Select(d => d.Code).ToList();
}
