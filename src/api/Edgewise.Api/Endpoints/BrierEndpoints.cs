using Edgewise.Api.Services.Coach;
using Edgewise.Domain.Engines.Calibration;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>/api/brier — Brier forecast CRUD, resolution scoring and the calibration report.</summary>
public sealed class BrierEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/brier").RequireAuthorization();

        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/calibration", GetCalibration);
        group.MapGet("/{id:guid}", GetOne);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapPost("/{id:guid}/resolve", Resolve);
    }

    private static async Task<IResult> List(EdgewiseDbContext db, bool? resolved, CancellationToken ct)
    {
        var query = db.BrierForecasts.AsQueryable();
        if (resolved == true)
        {
            query = query.Where(f => f.ResolvedAt != null);
        }
        else if (resolved == false)
        {
            query = query.Where(f => f.ResolvedAt == null);
        }

        var forecasts = await query.OrderByDescending(f => f.ResolutionDate).Take(200).ToListAsync(ct);
        return Results.Ok(forecasts.Select(ToDto).ToList());
    }

    private static async Task<IResult> Create(
        CreateBrierForecastRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        ValidateProbability(request.PUser, "pUser");
        ValidateProbability(request.PMarket, "pMarket");
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            throw ApiException.BadRequest("invalid_question", "question is required.");
        }

        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        var forecast = new BrierForecast
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TradeId = request.TradeId,
            Question = request.Question.Trim(),
            ResolutionDate = AsUtc(request.ResolutionDate),
            RulesUrl = request.RulesUrl,
            PUser = request.PUser,
            PMarket = request.PMarket,
            MakerTaker = request.MakerTaker,
        };
        db.BrierForecasts.Add(forecast);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/brier/{forecast.Id}", ToDto(forecast));
    }

    private static async Task<IResult> GetOne(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var forecast = await Require(db, id, ct);
        return Results.Ok(ToDto(forecast));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateBrierForecastRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var forecast = await Require(db, id, ct);
        if (forecast.ResolvedAt is not null)
        {
            throw ApiException.Conflict("already_resolved", "A resolved forecast cannot be edited.");
        }

        if (request.Question is not null)
        {
            forecast.Question = request.Question.Trim();
        }

        if (request.ResolutionDate is DateTime rd)
        {
            forecast.ResolutionDate = AsUtc(rd);
        }

        if (request.PUser is decimal pu)
        {
            ValidateProbability(pu, "pUser");
            forecast.PUser = pu;
        }

        if (request.PMarket is decimal pm)
        {
            ValidateProbability(pm, "pMarket");
            forecast.PMarket = pm;
        }

        if (request.RulesUrl is not null)
        {
            forecast.RulesUrl = request.RulesUrl;
        }

        if (request.MakerTaker is not null)
        {
            forecast.MakerTaker = request.MakerTaker;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(forecast));
    }

    private static async Task<IResult> Delete(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var forecast = await Require(db, id, ct);
        db.BrierForecasts.Remove(forecast);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Resolve(
        Guid id, ResolveBrierForecastRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var forecast = await Require(db, id, ct);
        if (forecast.ResolvedAt is not null)
        {
            throw ApiException.Conflict("already_resolved", "Forecast is already resolved.");
        }

        forecast.Outcome = request.Outcome;
        forecast.BrierScore = CalibrationEngine.BrierScore(forecast.PUser, request.Outcome);
        forecast.ResolvedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(forecast));
    }

    private static async Task<IResult> GetCalibration(
        EdgewiseDbContext db, CoachOrchestrator orchestrator, CancellationToken ct)
    {
        var resolved = await db.BrierForecasts
            .Where(f => f.Outcome != null && f.ResolvedAt != null)
            .ToListAsync(ct);
        if (resolved.Count == 0)
        {
            return Results.Ok(new { report = (CalibrationReport?)null, insight = (InsightDto?)null });
        }

        var report = CalibrationEngine.BuildReport(
            [.. resolved.Select(f => new ForecastResolution(f.PUser, f.PMarket, f.Outcome!.Value, f.ResolvedAt!.Value))]);
        var insight = await orchestrator.GenerateCalibrationInsightAsync(report, ct);
        return Results.Ok(new { report, insight = CoachJson.ToDto(insight) });
    }

    private static async Task<BrierForecast> Require(EdgewiseDbContext db, Guid id, CancellationToken ct) =>
        await db.BrierForecasts.FirstOrDefaultAsync(f => f.Id == id, ct)
        ?? throw ApiException.NotFound("forecast_not_found", "Forecast not found.");

    private static void ValidateProbability(decimal p, string field)
    {
        if (p is < 0m or > 1m)
        {
            throw ApiException.BadRequest("invalid_probability", $"{field} must be within [0, 1].");
        }
    }

    private static DateTime AsUtc(DateTime at) => at.Kind switch
    {
        DateTimeKind.Utc => at,
        DateTimeKind.Local => at.ToUniversalTime(),
        _ => DateTime.SpecifyKind(at, DateTimeKind.Utc),
    };

    private static BrierForecastDto ToDto(BrierForecast f) => new(
        f.Id, f.TradeId, f.Question, f.ResolutionDate, f.RulesUrl, f.PUser, f.PMarket,
        f.MakerTaker, f.Outcome, f.BrierScore, f.ResolvedAt);
}
