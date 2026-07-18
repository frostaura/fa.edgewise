namespace Edgewise.Domain.Entities;

public class Bucket : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public BucketKind Kind { get; set; }
    public decimal TargetAllocPct { get; set; }
    public decimal ContributionSplitPct { get; set; }
    public long HighWaterMarkMinor { get; set; }
    public string Currency { get; set; } = "ZAR";
}

public class Account : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid BucketId { get; set; }
    public Venue Venue { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? CredentialsEnc { get; set; }
    public string? WalletAddress { get; set; }
    public AccountStatus Status { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncStatsJson { get; set; }
}

/// <summary>Global instrument catalogue — not user-owned.</summary>
public class Instrument
{
    public Guid Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AssetClass AssetClass { get; set; }
    public string? Exchange { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? ProviderSymbolsJson { get; set; }
}

public class Holding : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid BucketId { get; set; }
    public Guid InstrumentId { get; set; }
    public string? ThesisNotesMd { get; set; }
    public decimal? ManualGrowthRatePct { get; set; }
}

public class Lot
{
    public Guid Id { get; set; }
    public Guid HoldingId { get; set; }
    public Holding Holding { get; set; } = null!;
    public decimal Qty { get; set; }
    public long CostMinor { get; set; }
    public string CostCurrency { get; set; } = string.Empty;
    public DateTime AcquiredAt { get; set; }
    public string? Source { get; set; }
    public Guid? TradeId { get; set; }
}

public class CashFlow : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid BucketId { get; set; }
    public CashFlowType Type { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public string? Note { get; set; }
}

public class Dividend
{
    public Guid Id { get; set; }
    public Guid HoldingId { get; set; }
    public Holding Holding { get; set; } = null!;
    public DateOnly ExDate { get; set; }
    public DateOnly PayDate { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool Reinvested { get; set; }
    public Guid? DripLotId { get; set; }
}

public class Snapshot : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? BucketId { get; set; }
    public DateOnly Date { get; set; }
    public long EquityMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public long NetFlowMinor { get; set; }
}

public class Forecast : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AssumptionsJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
