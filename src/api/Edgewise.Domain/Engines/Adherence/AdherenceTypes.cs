namespace Edgewise.Domain.Engines.Adherence;

/// <summary>Direction of a trade.</summary>
public enum TradeDirection
{
    Long = 0,
    Short = 1,
}

/// <summary>How the trade was exited, as classified by the caller.</summary>
public enum ExitRuleOutcome
{
    MatchedRule = 0,
    EarlyDiscretionary = 1,
    StopHit = 2,
    TimeStop = 3,
}

/// <summary>A single version of the plan's stop price, effective from <paramref name="At"/>.</summary>
public readonly record struct StopVersion(decimal Stop, DateTime At);

/// <summary>A single fill (entry or exit leg).</summary>
public readonly record struct TradeFill(decimal Qty, decimal Price, DateTime At);

/// <summary>
/// Input snapshot for scoring a single trade against the Adherence Rubric.
/// All prices (fill prices, stop prices) are expressed in minor currency units so that
/// qty × price-distance is directly comparable to <see cref="BucketEquityMinor"/>.
/// Percentages (<see cref="RiskPct"/>, <see cref="HeatCapPct"/>, <see cref="OpenRiskAtEntryPct"/>)
/// are fractions: 0.01 == 1%.
/// </summary>
public sealed record AdherenceInput
{
    /// <summary>When the plan was created. Null means the trade was unplanned.</summary>
    public DateTime? PlanCreatedAt { get; init; }

    /// <summary>Timestamp of the first entry fill.</summary>
    public DateTime FirstFillAt { get; init; }

    /// <summary>Full stop-price version history of the plan, in any order.</summary>
    public IReadOnlyList<StopVersion> StopVersions { get; init; } = [];

    public TradeDirection Direction { get; init; } = TradeDirection.Long;

    public IReadOnlyList<TradeFill> EntryFills { get; init; } = [];

    public IReadOnlyList<TradeFill> ExitFills { get; init; } = [];

    /// <summary>The stop price the plan committed to (minor units).</summary>
    public decimal PlannedStopPrice { get; init; }

    /// <summary>Planned position size in quantity.</summary>
    public decimal PlanSizeQty { get; init; }

    /// <summary>Largest actual position quantity reached during the trade.</summary>
    public decimal ActualMaxPositionQty { get; init; }

    /// <summary>Bucket equity snapshot at entry, in minor currency units.</summary>
    public long BucketEquityMinor { get; init; }

    /// <summary>Per-trade risk budget as a fraction of bucket equity (0.01 == 1%).</summary>
    public decimal RiskPct { get; init; }

    /// <summary>Maximum allowed total open risk as a fraction of bucket equity.</summary>
    public decimal HeatCapPct { get; init; }

    /// <summary>Total open risk across positions at the moment of entry, as a fraction of bucket equity.</summary>
    public decimal OpenRiskAtEntryPct { get; init; }

    /// <summary>Self-reported: did the trader confirm the entry-trigger checklist?</summary>
    public bool TriggerChecklistConfirmed { get; init; } = true;

    /// <summary>Caller-classified exit outcome.</summary>
    public ExitRuleOutcome ExitRule { get; init; } = ExitRuleOutcome.MatchedRule;

    /// <summary>Minutes elapsed since the prior loss in the same instrument class; null when no prior loss.</summary>
    public int? MinutesSincePriorLossSameClass { get; init; }

    /// <summary>True when the trade was held through a flagged red (news) event.</summary>
    public bool TradedThroughRedEvent { get; init; }

    /// <summary>True when a circuit breaker was active and the trader overrode it.</summary>
    public bool CircuitBreakerOverride { get; init; }
}

/// <summary>A single deduction applied by the rubric.</summary>
public sealed record AdherenceDeduction(string Code, int Points, string Evidence);

/// <summary>Result of scoring a trade against the rubric.</summary>
public sealed record AdherenceScore(
    int Score,
    string Grade,
    IReadOnlyList<AdherenceDeduction> Deductions,
    string RubricVersion);

/// <summary>A rubric rule described as data, so the API can serve the rubric itself.</summary>
public sealed record RubricRule(string Code, int Points, string Description, bool IsSelfReport);
