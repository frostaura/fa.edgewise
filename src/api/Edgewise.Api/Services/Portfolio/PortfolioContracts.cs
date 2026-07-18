using Edgewise.Domain.Entities;

namespace Edgewise.Api.Services.Portfolio;

// ---------------------------------------------------------------- conventions
// Money is minor units (cents) in the user's base currency unless a Currency
// field says otherwise. Percentages are FRACTIONS on the wire (0.10 = 10%);
// entity columns that store percent points are converted at this boundary.

public static class Pct
{
    public static decimal ToFraction(decimal points) => points / 100m;
    public static decimal ToPoints(decimal fraction) => fraction * 100m;
}

// ------------------------------------------------------------------- buckets
public sealed record BucketDto(
    Guid Id,
    string Name,
    BucketKind Kind,
    decimal TargetAllocPct,
    decimal ContributionSplitPct,
    long HighWaterMarkMinor,
    string Currency);

public sealed record CreateBucketRequest(
    string Name, BucketKind Kind, decimal TargetAllocPct, decimal ContributionSplitPct, string? Currency);

public sealed record UpdateBucketRequest(string? Name, decimal? TargetAllocPct, decimal? ContributionSplitPct);

// ------------------------------------------------------------------ holdings
public sealed record InstrumentDto(
    Guid Id, string Symbol, string Name, AssetClass AssetClass, string? Exchange, string Currency);

public sealed record LotDto(
    Guid Id, decimal Qty, long CostMinor, string CostCurrency, DateTime AcquiredAt, string? Source, Guid? TradeId);

public sealed record HoldingDto(
    Guid Id,
    Guid BucketId,
    InstrumentDto Instrument,
    string? ThesisNotesMd,
    decimal? ManualGrowthRatePct,
    decimal Qty,
    long CostBasisMinor,
    long? AvgCostMinor,
    long ValueMinor,
    long UnrealisedPnlMinor,
    decimal? UnrealisedPnlPct,
    decimal BucketWeightPct,
    decimal TotalWeightPct,
    bool Stale,
    decimal? Price,
    DateTime? PriceAsOf,
    int LotCount,
    IReadOnlyList<LotDto>? Lots = null);

public sealed record CreateHoldingRequest(
    Guid BucketId, Guid InstrumentId, string? ThesisNotesMd, decimal? ManualGrowthRatePct);

public sealed record UpdateHoldingRequest(
    Guid? BucketId, decimal? ManualGrowthRatePct, bool? ClearManualGrowthRate);

public sealed record UpdateThesisRequest(string? ThesisNotesMd);

public sealed record LotRequest(
    decimal Qty, long CostMinor, string? CostCurrency, DateTime AcquiredAt, string? Source);

// ----------------------------------------------------------------- cashflows
public sealed record CashFlowDto(
    Guid Id, Guid BucketId, CashFlowType Type, long AmountMinor, string Currency, DateTime At, string? Note);

/// <summary>AmountMinor is always positive; Transfer additionally requires ToBucketId.</summary>
public sealed record CreateCashFlowRequest(
    CashFlowType Type, Guid BucketId, Guid? ToBucketId, long AmountMinor, string? Currency, DateTime? At, string? Note);

public sealed record UpdateCashFlowRequest(DateTime? At, string? Note);

// ----------------------------------------------------------------- dividends
public sealed record DividendDto(
    Guid Id,
    Guid HoldingId,
    DateOnly ExDate,
    DateOnly PayDate,
    long AmountMinor,
    string Currency,
    bool Reinvested,
    Guid? DripLotId);

public sealed record CreateDividendRequest(
    Guid HoldingId,
    DateOnly ExDate,
    DateOnly PayDate,
    long AmountMinor,
    string? Currency,
    bool Reinvested,
    decimal? DripQty);

public sealed record UpdateDividendRequest(DateOnly? ExDate, DateOnly? PayDate, long? AmountMinor);

public sealed record UpcomingExDateDto(DateTime At, string Title);

public sealed record DividendSummaryRowDto(
    Guid HoldingId,
    Guid InstrumentId,
    string Symbol,
    string InstrumentName,
    long TotalReceivedMinor,
    long Trailing12MoMinor,
    decimal? YieldOnCost,
    long Projected12MoMinor,
    IReadOnlyList<UpcomingExDateDto> UpcomingExDates);

public sealed record DividendSummaryDto(
    string BaseCurrency,
    long TotalReceivedMinor,
    long Projected12MoMinor,
    IReadOnlyList<DividendSummaryRowDto> Holdings);

// ----------------------------------------------------------------- portfolio
public enum LadderState
{
    Normal,
    RiskHalved,
    Paused,
    PaperProposed,
}

public sealed record LadderThresholdsDto(decimal RiskHalvedPct, decimal PausedPct, decimal PaperPct);

public sealed record LadderStateDto(
    LadderState State, decimal DrawdownPct, long HwmMinor, long CurrentMinor, LadderThresholdsDto Thresholds);

public sealed record BucketSummaryDto(
    Guid Id,
    string Name,
    BucketKind Kind,
    long ValueMinor,
    decimal TargetAllocPct,
    decimal ActualAllocPct,
    decimal ContributionSplitPct,
    bool DriftFlag,
    long HighWaterMarkMinor,
    bool Stale);

public sealed record PortfolioSummaryDto(
    string BaseCurrency,
    long TotalValueMinor,
    long TotalCostMinor,
    long UnrealisedPnlMinor,
    long? TodayChangeMinor,
    IReadOnlyList<BucketSummaryDto> PerBucket,
    LadderStateDto LadderState);

public sealed record RatchetSuggestionDto(
    long SuggestedAmountMinor, long ProfitAboveHwmMinor, long HwmMinor, long CurrentMinor);

public sealed record AcceptRatchetRequest(long AmountMinor);

public sealed record AcceptRatchetResultDto(
    Guid FromCashFlowId, Guid ToCashFlowId, long NewHighWaterMarkMinor, LadderStateDto LadderState);

public sealed record PerformancePointDto(DateOnly Date, decimal PeriodReturn, decimal CumulativeReturn);

public sealed record PerformanceDto(
    string Basis,
    IReadOnlyList<PerformancePointDto> Points,
    decimal? TotalReturn,
    decimal? AnnualizedReturn,
    decimal? Xirr,
    decimal? MaxDrawdown);

public sealed record AllocationSliceDto(string Key, string Label, long ValueMinor, decimal Pct);

public sealed record AllocationDto(
    string BaseCurrency,
    long TotalValueMinor,
    IReadOnlyList<AllocationSliceDto> PerBucket,
    IReadOnlyList<AllocationSliceDto> PerAssetClass);

public sealed record NetworthPointDto(DateOnly Date, long EquityMinor, long NetFlowMinor);

public sealed record SnapshotRunResultDto(DateOnly Date, int RowsWritten);

// ----------------------------------------------------------------- forecasts
/// <summary>Growth/vol/haircut/increase are fractions (0.08 = 8%/year).</summary>
public sealed record ForecastAssetAssumptionDto(
    string Key, long CurrentValueMinor, decimal AnnualGrowthPct, decimal? AnnualVolPct);

public sealed record ForecastContributionDto(
    long AmountMinor, decimal AnnualIncreasePct, Dictionary<string, decimal> Splits);

public sealed record ForecastAssumptionsDto(
    List<ForecastAssetAssumptionDto> Assets,
    ForecastContributionDto Contribution,
    bool Reinvest,
    int HorizonYears,
    decimal? HaircutPct);

public sealed record DeterministicBandDto(int Year, long BearMinor, long BaseMinor, long BullMinor);

public sealed record MonteCarloBandDto(
    int Year, long P5Minor, long P25Minor, long P50Minor, long P75Minor, long P95Minor);

public sealed record ForecastResultDto(
    DateTime RanAt,
    bool MonteCarlo,
    int? Paths,
    int? Seed,
    List<DeterministicBandDto> Deterministic,
    List<MonteCarloBandDto>? MonteCarloBands);

public sealed record ForecastDto(
    Guid Id, string Name, ForecastAssumptionsDto Assumptions, ForecastResultDto? Result, DateTime CreatedAt);

public sealed record SaveForecastRequest(string Name, ForecastAssumptionsDto Assumptions);

public sealed record ForecastSeedNoteDto(string Key, Guid HoldingId, string Symbol, bool GrowthCapped, bool CagrFromLots);

public sealed record ForecastSeedDto(ForecastAssumptionsDto Assumptions, IReadOnlyList<ForecastSeedNoteDto> Notes);
