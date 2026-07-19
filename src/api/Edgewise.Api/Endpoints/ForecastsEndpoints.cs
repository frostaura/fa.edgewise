using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// /api/forecasts — named forecast CRUD, engine runs (deterministic bands always,
/// Monte Carlo percentiles on request) and the seed-from-portfolio helper.
/// Assumptions are stored as JSON exactly as the client sent them (fractions).
/// </summary>
public sealed class ForecastsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/forecasts").RequireAuthorization();
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/seed", Seed);
        group.MapGet("/{id:guid}", Get);
        group.MapPatch("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapPost("/{id:guid}/run", Run);
    }

    private static ForecastDto ToDto(Forecast f) => new(
        f.Id,
        f.Name,
        ForecastRunner.ParseAssumptions(f.AssumptionsJson),
        ForecastRunner.ParseResult(f.ResultJson),
        f.CreatedAt);

    private static async Task<IResult> List(EdgewiseDbContext db, CancellationToken ct)
    {
        var forecasts = await db.Forecasts.AsNoTracking().OrderByDescending(f => f.CreatedAt).ToListAsync(ct);
        return Results.Ok(forecasts.Select(ToDto).ToList());
    }

    private static async Task<IResult> Get(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var forecast = await db.Forecasts.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw ApiException.NotFound("forecast_not_found", "Forecast not found.");
        return Results.Ok(ToDto(forecast));
    }

    private static async Task<IResult> Create(
        SaveForecastRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw ApiException.BadRequest("validation_error", "name is required.");
        }

        var assumptions = ForecastRunner.Validate(request.Assumptions);
        var forecast = new Forecast
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name.Trim(),
            AssumptionsJson = ForecastRunner.SerializeAssumptions(assumptions),
            CreatedAt = DateTime.UtcNow,
        };
        db.Forecasts.Add(forecast);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/forecasts/{forecast.Id}", ToDto(forecast));
    }

    private static async Task<IResult> Update(
        Guid id, SaveForecastRequest request, EdgewiseDbContext db, CancellationToken ct)
    {
        var forecast = await db.Forecasts.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw ApiException.NotFound("forecast_not_found", "Forecast not found.");

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            forecast.Name = request.Name.Trim();
        }

        if (request.Assumptions is not null)
        {
            var assumptions = ForecastRunner.Validate(request.Assumptions);
            forecast.AssumptionsJson = ForecastRunner.SerializeAssumptions(assumptions);
            forecast.ResultJson = null; // stale result no longer matches the assumptions
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(forecast));
    }

    private static async Task<IResult> Delete(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var forecast = await db.Forecasts.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw ApiException.NotFound("forecast_not_found", "Forecast not found.");
        db.Forecasts.Remove(forecast);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Run(
        Guid id, bool? monteCarlo, int? paths, int? seed, EdgewiseDbContext db, CancellationToken ct)
    {
        var forecast = await db.Forecasts.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw ApiException.NotFound("forecast_not_found", "Forecast not found.");

        var assumptions = ForecastRunner.ParseAssumptions(forecast.AssumptionsJson);
        var result = ForecastRunner.Run(assumptions, monteCarlo ?? false, paths ?? 1000, seed ?? 0);
        forecast.ResultJson = ForecastRunner.SerializeResult(result);
        await db.SaveChangesAsync(ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> Seed(
        ForecastRunner runner, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");
        return Results.Ok(await runner.SeedFromPortfolioAsync(userId, ct));
    }
}
