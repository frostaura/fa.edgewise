namespace Edgewise.Domain.Entities;

/// <summary>Shipped defaults have <see cref="UserId"/> == null; user copies set it.</summary>
public class PlaybookTemplate
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? PrefillJson { get; set; }
    public string? ChecklistJson { get; set; }
}

public class TradePlan : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid InstrumentId { get; set; }
    public Guid BucketId { get; set; }
    public Guid RiskProfileId { get; set; }
    public TradeDirection Direction { get; set; }
    public string? SetupTag { get; set; }
    public string? TriggerText { get; set; }
    public decimal StopPrice { get; set; }
    public string? TargetRuleJson { get; set; }
    public decimal SizeQty { get; set; }
    public bool SizeOverridden { get; set; }
    public string? InvalidationNote { get; set; }
    public bool IsPaper { get; set; }
    public TradePlanStatus Status { get; set; }
    public string? CockpitCheckJson { get; set; }
    public string? ChecklistConfirmedJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class TradePlanVersion
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public TradePlan Plan { get; set; } = null!;
    public int Version { get; set; }
    public string FieldsJson { get; set; } = "{}";
    public DateTime At { get; set; }
}

public class Trade : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? PlanId { get; set; }
    public Guid InstrumentId { get; set; }
    public Guid BucketId { get; set; }
    public Guid? AccountId { get; set; }
    public TradeDirection Direction { get; set; }
    public TradeStatus Status { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public decimal Qty { get; set; }
    public decimal AvgEntryPrice { get; set; }
    public decimal? AvgExitPrice { get; set; }
    public long RealisedPnlMinor { get; set; }
    public long FeesMinor { get; set; }
    public long FundingMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal? RPlanned { get; set; }
    public decimal? RRealised { get; set; }
    public decimal? MaePct { get; set; }
    public decimal? MfePct { get; set; }
    public long? HoldingSeconds { get; set; }
    public EmotionTag EmotionTag { get; set; }
    public bool IsPaper { get; set; }
    public Guid? StrategyId { get; set; }
    public string? StrategyStateAtEntry { get; set; }
    public PositionMethod PositionMethod { get; set; }
}

public class Fill : IUserOwned
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid UserId { get; set; }
    public Guid InstrumentId { get; set; }
    public Guid? TradeId { get; set; }
    public FillSide Side { get; set; }
    public decimal Qty { get; set; }
    public decimal Price { get; set; }
    public long FeeMinor { get; set; }
    public string FeeCurrency { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public FillSource Source { get; set; }
    public string SourceHash { get; set; } = string.Empty;
    public string? RawPayloadJson { get; set; }
    public MatchStatus MatchStatus { get; set; }
}

public class JournalEntry
{
    public Guid Id { get; set; }
    public Guid TradeId { get; set; }
    public Trade Trade { get; set; } = null!;
    public string NotesMd { get; set; } = string.Empty;
    public DateTime At { get; set; }
}

public class Attachment
{
    public Guid Id { get; set; }
    public Guid JournalEntryId { get; set; }
    public JournalEntry JournalEntry { get; set; } = null!;
    public string FilePath { get; set; } = string.Empty;
    public string Mime { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

public class AdherenceResult
{
    public Guid Id { get; set; }
    public Guid TradeId { get; set; }
    public Trade Trade { get; set; } = null!;
    public int RubricVersion { get; set; }
    public int Score { get; set; }
    public string Grade { get; set; } = string.Empty;
    public string? DeductionsJson { get; set; }
    public DateTime ComputedAt { get; set; }
}

public class Tag : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>Join table; composite PK (TradeId, TagId).</summary>
public class TradeTag
{
    public Guid TradeId { get; set; }
    public Trade Trade { get; set; } = null!;
    public Guid TagId { get; set; }
}

public class DecisionLog : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DecisionKind Kind { get; set; }
    public Guid? InstrumentId { get; set; }
    public Guid? PlanId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime At { get; set; }
}

public class OverrideLog : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public OverrideKind Kind { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime At { get; set; }
}
