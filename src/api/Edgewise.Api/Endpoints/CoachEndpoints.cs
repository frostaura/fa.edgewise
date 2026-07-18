using Edgewise.Api.Services.Coach;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// /api/coach — insights, feedback, on-demand post-mortems, weekly reviews, dossiers,
/// gateway status and the ask-my-journal SSE chat.
/// </summary>
public sealed class CoachEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/coach").RequireAuthorization();

        group.MapGet("/insights", ListInsights);
        group.MapPost("/insights/{id:guid}/feedback", SubmitFeedback);
        group.MapPost("/trades/{id:guid}/postmortem", GeneratePostMortem);
        group.MapGet("/weekly-reviews/{weekStart}", GetWeeklyReview);
        group.MapPost("/weekly-reviews/{weekStart}", UpdateWeeklyReview);
        group.MapGet("/dossier/{instrumentId:guid}", GetDossier);
        group.MapGet("/status", GetStatus);
        group.MapPost("/chat", Chat);
    }

    private static async Task<IResult> ListInsights(
        EdgewiseDbContext db, string? type, Guid? tradeId, int? limit, CancellationToken ct)
    {
        var query = db.Insights.Where(i => i.SupersededById == null);
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<InsightType>(type, ignoreCase: true, out var parsed))
            {
                throw ApiException.BadRequest("invalid_type", "Unknown insight type.");
            }

            query = query.Where(i => i.Type == parsed);
        }

        if (tradeId is Guid tid)
        {
            query = query.Where(i => i.TradeId == tid);
        }

        var take = Math.Clamp(limit ?? 50, 1, 200);
        var insights = await query.OrderByDescending(i => i.CreatedAt).Take(take).ToListAsync(ct);
        return Results.Ok(insights.Select(CoachJson.ToDto).ToList());
    }

    private static async Task<IResult> SubmitFeedback(
        Guid id, InsightFeedbackRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var insight = await db.Insights.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw ApiException.NotFound("insight_not_found", "Insight not found.");

        insight.Feedback = request.Feedback;
        insight.FeedbackReason = request.Feedback == InsightFeedback.Down ? request.Reason : null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(CoachJson.ToDto(insight));
    }

    private static async Task<IResult> GeneratePostMortem(
        Guid id, CoachOrchestrator orchestrator, CancellationToken ct)
    {
        var insight = await orchestrator.GeneratePostMortemAsync(id, ct);
        return Results.Ok(CoachJson.ToDto(insight));
    }

    private static async Task<IResult> GetWeeklyReview(
        string weekStart, EdgewiseDbContext db, CoachOrchestrator orchestrator, CancellationToken ct)
    {
        var week = ParseWeekStart(weekStart);
        var review = await db.WeeklyReviews.FirstOrDefaultAsync(w => w.WeekStartDate == week, ct);
        Insight? pack = null;
        if (review?.PackInsightId is Guid packId)
        {
            pack = await db.Insights.FirstOrDefaultAsync(i => i.Id == packId, ct);
        }

        if (review is null || pack is null)
        {
            (pack, review) = await orchestrator.GenerateWeeklyPackAsync(week, ct);
        }

        var past = await db.WeeklyReviews
            .Where(w => w.WeekStartDate < week)
            .OrderByDescending(w => w.WeekStartDate)
            .Take(12)
            .ToListAsync(ct);

        return Results.Ok(new WeeklyReviewStateDto(
            ToDto(review),
            pack is null ? null : CoachJson.ToDto(pack),
            [.. past.Select(ToDto)]));
    }

    private static async Task<IResult> UpdateWeeklyReview(
        string weekStart,
        UpdateWeeklyReviewRequest request,
        EdgewiseDbContext db,
        CoachOrchestrator orchestrator,
        CancellationToken ct)
    {
        var week = ParseWeekStart(weekStart);
        var review = await db.WeeklyReviews.FirstOrDefaultAsync(w => w.WeekStartDate == week, ct);
        if (review is null)
        {
            (_, review) = await orchestrator.GenerateWeeklyPackAsync(week, ct);
        }

        if (request.UserEditsMd is not null)
        {
            review.UserEditsMd = request.UserEditsMd;
        }

        if (request.FocusCommitment is not null)
        {
            review.FocusCommitment = request.FocusCommitment;
        }

        if (request.Complete && review.CompletedAt is null)
        {
            review.CompletedAt = DateTime.UtcNow;
            var previous = await db.WeeklyReviews
                .FirstOrDefaultAsync(w => w.WeekStartDate == week.AddDays(-7), ct);
            review.StreakCount = previous?.CompletedAt is not null ? previous.StreakCount + 1 : 1;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(review));
    }

    private static async Task<IResult> GetDossier(
        Guid instrumentId, EdgewiseDbContext db, CoachOrchestrator orchestrator, CancellationToken ct)
    {
        // Cached for 24h via the newest non-superseded Dossier insight for this instrument.
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var cached = (await db.Insights
                .Where(i => i.Type == InsightType.Dossier && i.SupersededById == null && i.CreatedAt >= cutoff)
                .OrderByDescending(i => i.CreatedAt)
                .ToListAsync(ct))
            .FirstOrDefault(i => CoachOrchestrator.ReadMeta(i.ContentJson, "instrumentId") == instrumentId.ToString());

        var insight = cached ?? await orchestrator.GenerateDossierAsync(instrumentId, ct);
        return Results.Ok(CoachJson.ToDto(insight));
    }

    private static async Task<IResult> GetStatus(LlmGateway gateway, CancellationToken ct)
    {
        var status = await gateway.GetStatusAsync(ct);
        return Results.Ok(new CoachStatusDto(
            status.Enabled, status.Provider, status.BudgetUsedTokens, status.BudgetTokens, status.OptedOut));
    }

    private static async Task Chat(
        HttpContext context, ChatRequest request, CoachChatService chat, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw ApiException.BadRequest("empty_message", "message is required.");
        }

        await chat.HandleAsync(context, request, ct);
    }

    private static DateOnly ParseWeekStart(string weekStart)
    {
        if (!DateOnly.TryParseExact(weekStart, "yyyy-MM-dd", out var week))
        {
            throw ApiException.BadRequest("invalid_week_start", "weekStart must be a yyyy-MM-dd date.");
        }

        if (week.DayOfWeek != DayOfWeek.Monday)
        {
            throw ApiException.BadRequest("invalid_week_start", "weekStart must be a Monday.");
        }

        return week;
    }

    private static WeeklyReviewDto ToDto(WeeklyReview review) => new(
        review.Id, review.WeekStartDate, review.PackInsightId, review.UserEditsMd,
        review.FocusCommitment, review.CompletedAt, review.StreakCount);
}
