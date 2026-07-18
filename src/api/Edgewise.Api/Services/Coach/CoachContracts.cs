using System.Text.Json.Nodes;
using Edgewise.Domain.Entities;

namespace Edgewise.Api.Services.Coach;

/// <summary>A resolved citation: the machine ref plus a human label for provenance chips.</summary>
public sealed record CitationDto(string Ref, string Label);

public sealed record InsightDto(
    Guid Id,
    InsightType Type,
    Guid? TradeId,
    JsonNode? Content,
    IReadOnlyList<CitationDto> Citations,
    string? ModelTag,
    string? PromptVersion,
    InsightFeedback Feedback,
    string? FeedbackReason,
    DateTime CreatedAt);

public sealed record InsightFeedbackRequest(InsightFeedback Feedback, string? Reason = null);

public sealed record CoachStatusDto(
    bool Enabled,
    string Provider,
    long BudgetUsedTokens,
    long BudgetTokens,
    bool OptedOut);

public sealed record WeeklyReviewDto(
    Guid Id,
    DateOnly WeekStartDate,
    Guid? PackInsightId,
    string? UserEditsMd,
    string? FocusCommitment,
    DateTime? CompletedAt,
    int StreakCount);

public sealed record WeeklyReviewStateDto(
    WeeklyReviewDto Review,
    InsightDto? Pack,
    IReadOnlyList<WeeklyReviewDto> PastReviews);

public sealed record UpdateWeeklyReviewRequest(
    string? UserEditsMd = null,
    string? FocusCommitment = null,
    bool Complete = false);

public sealed record ChatMessageDto(string Role, string Text);

public sealed record ChatRequest(string Message, IReadOnlyList<ChatMessageDto>? History = null);

// ------------------------------------------------------------------- brier

public sealed record CreateBrierForecastRequest(
    string Question,
    DateTime ResolutionDate,
    decimal PUser,
    decimal PMarket,
    string? RulesUrl = null,
    string? MakerTaker = null,
    Guid? TradeId = null);

public sealed record UpdateBrierForecastRequest(
    string? Question = null,
    DateTime? ResolutionDate = null,
    decimal? PUser = null,
    decimal? PMarket = null,
    string? RulesUrl = null,
    string? MakerTaker = null);

public sealed record ResolveBrierForecastRequest(bool Outcome);

public sealed record BrierForecastDto(
    Guid Id,
    Guid? TradeId,
    string Question,
    DateTime ResolutionDate,
    string? RulesUrl,
    decimal PUser,
    decimal PMarket,
    string? MakerTaker,
    bool? Outcome,
    decimal? BrierScore,
    DateTime? ResolvedAt);
