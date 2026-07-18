using Edgewise.Domain.Entities;

namespace Edgewise.Api.Services.Journal;

// ---------------------------------------------------------------- shared

public sealed record InstrumentSummaryDto(
    Guid Id, string Symbol, string Name, AssetClass AssetClass, string Currency, string? Exchange);

// ----------------------------------------------------------------- plans

public sealed record PlanDto(
    Guid Id,
    Guid? TemplateId,
    Guid InstrumentId,
    Guid BucketId,
    Guid RiskProfileId,
    TradeDirection Direction,
    string? SetupTag,
    string? TriggerText,
    decimal StopPrice,
    string? TargetRuleJson,
    decimal SizeQty,
    bool SizeOverridden,
    string? InvalidationNote,
    bool IsPaper,
    TradePlanStatus Status,
    string? CockpitCheckJson,
    string? ChecklistConfirmedJson,
    DateTime CreatedAt,
    InstrumentSummaryDto? Instrument);

public sealed record PlanVersionDto(int Version, string FieldsJson, DateTime At);

public sealed record CreatePlanRequest(
    Guid? TemplateId,
    Guid InstrumentId,
    Guid BucketId,
    Guid? RiskProfileId,
    TradeDirection Direction,
    string? SetupTag,
    string? TriggerText,
    decimal StopPrice,
    string? TargetRuleJson,
    decimal? SizeQty,
    bool? SizeOverridden,
    string? InvalidationNote,
    bool IsPaper,
    string? ChecklistConfirmedJson,
    string? CockpitCheckJson,
    decimal? EntryPrice);

public sealed record PatchPlanRequest(
    TradeDirection? Direction,
    string? SetupTag,
    string? TriggerText,
    decimal? StopPrice,
    string? TargetRuleJson,
    decimal? SizeQty,
    string? InvalidationNote,
    string? ChecklistConfirmedJson,
    TradePlanStatus? Status);

public sealed record SizePreviewDto(decimal SuggestedQty, long RValueMinor, long NotionalMinor, long BucketEquityMinor, decimal RiskPct);

public sealed record PromotePlanRequest(Guid? TradeId, List<PromoteFillRequest>? Fills);

public sealed record PromoteFillRequest(
    Guid? AccountId, FillSide Side, decimal Qty, decimal Price, long FeeMinor, DateTime At);

public sealed record PlanTemplateDto(
    Guid Id, Guid? UserId, string Name, string? Description, string? PrefillJson, string? ChecklistJson, bool Shipped);

public sealed record UpsertTemplateRequest(string Name, string? Description, string? PrefillJson, string? ChecklistJson);

/// <summary>One-round-trip lookup payload for the plan composer.</summary>
public sealed record PlanLookupsDto(
    IReadOnlyList<PlanTemplateDto> Templates,
    IReadOnlyList<InstrumentSummaryDto> Instruments,
    IReadOnlyList<BucketSummaryDto> Buckets,
    IReadOnlyList<RiskProfileSummaryDto> RiskProfiles,
    IReadOnlyList<AccountSummaryDto> Accounts);

public sealed record BucketSummaryDto(Guid Id, string Name, BucketKind Kind, string Currency);

public sealed record AccountSummaryDto(Guid Id, string Name, Venue Venue, Guid BucketId);

public sealed record RiskProfileSummaryDto(Guid Id, string Name, decimal RiskPct, decimal HeatCapPct, bool IsActive);

public sealed record CreateInstrumentRequest(string Symbol, string? Name, AssetClass? AssetClass, string? Currency);

// ---------------------------------------------------------------- trades

public sealed record TradeListItemDto(
    Guid Id,
    Guid? PlanId,
    Guid InstrumentId,
    string InstrumentSymbol,
    TradeDirection Direction,
    TradeStatus Status,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    decimal Qty,
    decimal AvgEntryPrice,
    decimal? AvgExitPrice,
    long RealisedPnlMinor,
    string Currency,
    decimal? RRealised,
    EmotionTag EmotionTag,
    bool IsPaper,
    string? SetupTag,
    int? AdherenceScore,
    string? AdherenceGrade,
    IReadOnlyList<string> Tags);

public sealed record TradeDto(
    Guid Id,
    Guid? PlanId,
    Guid InstrumentId,
    Guid BucketId,
    Guid? AccountId,
    TradeDirection Direction,
    TradeStatus Status,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    decimal Qty,
    decimal AvgEntryPrice,
    decimal? AvgExitPrice,
    long RealisedPnlMinor,
    long FeesMinor,
    long FundingMinor,
    string Currency,
    decimal? RPlanned,
    decimal? RRealised,
    decimal? MaePct,
    decimal? MfePct,
    long? HoldingSeconds,
    EmotionTag EmotionTag,
    bool IsPaper,
    InstrumentSummaryDto? Instrument);

public sealed record FillDto(
    Guid Id,
    Guid AccountId,
    Guid InstrumentId,
    Guid? TradeId,
    FillSide Side,
    decimal Qty,
    decimal Price,
    long FeeMinor,
    string FeeCurrency,
    DateTime At,
    FillSource Source,
    MatchStatus MatchStatus);

public sealed record JournalEntryDto(Guid Id, string NotesMd, DateTime At);

public sealed record AdherenceResultDto(
    Guid Id, int RubricVersion, int Score, string Grade, string? DeductionsJson, DateTime ComputedAt);

public sealed record InsightDto(Guid Id, InsightType Type, string ContentJson, DateTime CreatedAt);

public sealed record TradeDetailDto(
    TradeDto Trade,
    PlanDto? Plan,
    IReadOnlyList<PlanVersionDto> PlanVersions,
    IReadOnlyList<FillDto> Fills,
    IReadOnlyList<JournalEntryDto> JournalEntries,
    AdherenceResultDto? Adherence,
    IReadOnlyList<AdherenceResultDto> AdherenceHistory,
    IReadOnlyList<string> Tags,
    IReadOnlyList<InsightDto>? Insights);

public sealed record PatchTradeRequest(EmotionTag? EmotionTag, string? Notes, List<string>? Tags);

public sealed record CloseTradeRequest(
    List<PromoteFillRequest>? ExitFills,
    DateTime? ClosedAt,
    decimal? AvgExitPrice,
    Domain.Engines.Adherence.ExitRuleOutcome? ExitRule);

public sealed record RebuildTradesRequest(Guid AccountId, Guid InstrumentId);

public sealed record RebuildTradesResponse(int Removed, int Created, int SkippedWithEdits);

public sealed record BulkTagsRequest(List<Guid> TradeIds, List<string>? AddTags, List<string>? RemoveTags);

// ----------------------------------------------------------------- fills

public sealed record CreateFillRequest(
    Guid? AccountId,
    Guid InstrumentId,
    FillSide Side,
    decimal Qty,
    decimal Price,
    long FeeMinor,
    string? FeeCurrency,
    DateTime At,
    Guid? TradeId);

public sealed record InboxGroupDto(
    Guid InstrumentId,
    InstrumentSummaryDto? Instrument,
    Guid AccountId,
    string? AccountName,
    IReadOnlyList<FillDto> Fills,
    IReadOnlyList<PlanDto> SuggestedPlans,
    IReadOnlyList<InboxTradePreviewDto> TradePreview,
    IReadOnlyList<OpenTradeSummaryDto> OpenTrades);

public sealed record InboxTradePreviewDto(
    string TradeKey,
    TradeDirection Direction,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    decimal Qty,
    decimal AvgEntryPrice,
    decimal? AvgExitPrice,
    long RealisedPnlMinor,
    IReadOnlyList<string> FillIds);

public sealed record OpenTradeSummaryDto(
    Guid Id, TradeDirection Direction, DateTime OpenedAt, decimal Qty, decimal AvgEntryPrice);

public sealed record MatchFillRequest(Guid? PlanId, Guid? TradeId, bool CreateTrade);

// --------------------------------------------------------------- imports

public sealed record ImportPreviewDto(
    IReadOnlyList<string> Columns,
    Dictionary<string, string?> SuggestedMapping,
    Venue SuggestedVenue,
    IReadOnlyList<Dictionary<string, string>> SampleRows,
    IReadOnlyList<ImportMappingDto> SavedMappings);

public sealed record ImportMappingDto(Guid Id, Venue Venue, string Name, string MappingJson);

public sealed record ImportCommitResponse(int Imported, int Duplicates, IReadOnlyList<string> Errors);

// ------------------------------------------------------------- decisions

public sealed record CreateDecisionRequest(DecisionKind Kind, Guid? InstrumentId, Guid? PlanId, string Reason);

public sealed record DecisionDto(
    Guid Id, DecisionKind Kind, Guid? InstrumentId, Guid? PlanId, string Reason, DateTime At);
