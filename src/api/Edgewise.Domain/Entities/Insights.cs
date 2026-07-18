namespace Edgewise.Domain.Entities;

public class Insight : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TradeId { get; set; }
    public InsightType Type { get; set; }
    public string ContentJson { get; set; } = "{}";
    public string? CitationsJson { get; set; }
    public string? ModelTag { get; set; }
    public string? PromptVersion { get; set; }
    public InsightFeedback Feedback { get; set; }
    public string? FeedbackReason { get; set; }
    public Guid? SupersededById { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LlmRequestLog : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public long CostMicroUsd { get; set; }
    public DateTime At { get; set; }
    public bool Success { get; set; }
}

public class WeeklyReview : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateOnly WeekStartDate { get; set; }
    public Guid? PackInsightId { get; set; }
    public string? UserEditsMd { get; set; }
    public string? FocusCommitment { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int StreakCount { get; set; }
}

public class BrierForecast : IUserOwned
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TradeId { get; set; }
    public string Question { get; set; } = string.Empty;
    public DateTime ResolutionDate { get; set; }
    public string? RulesUrl { get; set; }
    public decimal PUser { get; set; }
    public decimal PMarket { get; set; }
    public string? MakerTaker { get; set; }
    public bool? Outcome { get; set; }
    public decimal? BrierScore { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
