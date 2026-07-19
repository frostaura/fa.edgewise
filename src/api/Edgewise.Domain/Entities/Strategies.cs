namespace Edgewise.Domain.Entities;

public class Strategy : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public StrategyState State { get; set; }
    public int CurrentVersion { get; set; } = 1;
}

public class StrategyVersion
{
    public Guid Id { get; set; }
    public Guid StrategyId { get; set; }
    public Strategy Strategy { get; set; } = null!;
    public int Version { get; set; }
    public string RuleTreeJson { get; set; } = "{}";
    public DateTime At { get; set; }
}

public class Backtest : IUserOwned
{
    public Guid Id { get; set; }
    public Guid StrategyVersionId { get; set; }
    public Guid UserId { get; set; }
    public string InstrumentIdsJson { get; set; } = "[]";
    public Timeframe Timeframe { get; set; }
    public DateTime RangeStart { get; set; }
    public DateTime RangeEnd { get; set; }
    public string? CostModelJson { get; set; }
    public BacktestStatus Status { get; set; }
    public string? ResultJson { get; set; }
    public string? HonestyJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PipelineStateChange
{
    public Guid Id { get; set; }
    public Guid StrategyId { get; set; }
    public Strategy Strategy { get; set; } = null!;
    public StrategyState FromState { get; set; }
    public StrategyState ToState { get; set; }
    public string? GatesEvidenceJson { get; set; }
    public DateTime At { get; set; }
}

public class ImportMapping : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Venue Venue { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MappingJson { get; set; } = "{}";
}
