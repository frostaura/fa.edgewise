using System.Text.Json;
using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

// --------------------------------------------------------------- contracts
// Percentages are FRACTIONS on the wire (0.01 = 1%), matching the portfolio
// endpoints; the RiskProfile entity stores percent points, converted here.
// Ladder thresholds are persisted to LadderThresholdsJson exactly as
// PortfolioService.GetLadderThresholdsAsync parses them:
// {"riskHalvedPct":0.05,"pausedPct":0.10,"paperPct":0.15} (fractions).

public sealed record RiskProfileDto(
    Guid Id,
    string Name,
    int Version,
    bool IsActive,
    decimal RiskPct,
    decimal HeatCapPct,
    decimal ClusterCapPct,
    decimal DailyStopPct,
    int DailyLossCountStop,
    decimal WeeklyStopPct,
    decimal MaxLeverage,
    decimal MinRR,
    int MaxPositions,
    LadderThresholdsDto LadderThresholds);

public sealed record CreateRiskProfileRequest(
    string Name,
    decimal RiskPct,
    decimal HeatCapPct,
    decimal ClusterCapPct,
    decimal DailyStopPct,
    int DailyLossCountStop,
    decimal WeeklyStopPct,
    decimal MaxLeverage,
    decimal MinRR,
    int MaxPositions,
    LadderThresholdsDto? LadderThresholds);

/// <summary>Updates never mutate a version in place: a new row (Version + 1, same Name) is inserted.</summary>
public sealed record UpdateRiskProfileRequest(
    decimal RiskPct,
    decimal HeatCapPct,
    decimal ClusterCapPct,
    decimal DailyStopPct,
    int DailyLossCountStop,
    decimal WeeklyStopPct,
    decimal MaxLeverage,
    decimal MinRR,
    int MaxPositions,
    LadderThresholdsDto? LadderThresholds);

/// <summary>
/// /api/risk-profiles — the guardrail configuration every engine reads.
/// GET lists the latest version of each named profile; PUT is versioned
/// (insert Version + 1, carrying the active flag); POST /{id}/activate makes a
/// profile the single active one; DELETE removes a profile (all versions) only
/// when no trade plan references any version of it.
/// </summary>
public sealed class RiskProfilesEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/risk-profiles").RequireAuthorization();
        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapPost("/{id:guid}/activate", Activate);
        group.MapDelete("/{id:guid}", Delete);
    }

    private static async Task<IResult> List(EdgewiseDbContext db, CancellationToken ct)
    {
        var profiles = await db.RiskProfiles.AsNoTracking().ToListAsync(ct);
        var latest = profiles
            .GroupBy(p => p.Name)
            .Select(g => g.OrderByDescending(p => p.Version).First())
            .OrderBy(p => p.Name)
            .Select(ToDto)
            .ToList();
        return Results.Ok(latest);
    }

    private static async Task<IResult> Get(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var profile = await db.RiskProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ApiException.NotFound("risk_profile_not_found", "Risk profile not found.");
        return Results.Ok(ToDto(profile));
    }

    private static async Task<IResult> Create(
        CreateRiskProfileRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 60)
        {
            throw ApiException.BadRequest("validation_error", "name is required (max 60 characters).");
        }

        var taken = await db.RiskProfiles.AsNoTracking()
            .AnyAsync(p => p.Name.ToLower() == name.ToLower(), ct);
        if (taken)
        {
            throw ApiException.Conflict("risk_profile_name_taken", "A risk profile with this name already exists.");
        }

        ValidateFields(
            request.RiskPct, request.HeatCapPct, request.ClusterCapPct, request.DailyStopPct,
            request.DailyLossCountStop, request.WeeklyStopPct, request.MaxLeverage, request.MinRR,
            request.MaxPositions, request.LadderThresholds);

        var profile = new RiskProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Version = 1,
            IsActive = false,
            RiskPct = Pct.ToPoints(request.RiskPct),
            HeatCapPct = Pct.ToPoints(request.HeatCapPct),
            ClusterCapPct = Pct.ToPoints(request.ClusterCapPct),
            DailyStopPct = Pct.ToPoints(request.DailyStopPct),
            DailyLossCountStop = request.DailyLossCountStop,
            WeeklyStopPct = Pct.ToPoints(request.WeeklyStopPct),
            MaxLeverage = request.MaxLeverage,
            MinRR = request.MinRR,
            MaxPositions = request.MaxPositions,
            LadderThresholdsJson = SerializeLadder(request.LadderThresholds ?? PortfolioService.DefaultLadderThresholds),
        };
        db.RiskProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/risk-profiles/{profile.Id}", ToDto(profile));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateRiskProfileRequest request, EdgewiseDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        var existing = await db.RiskProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ApiException.NotFound("risk_profile_not_found", "Risk profile not found.");

        ValidateFields(
            request.RiskPct, request.HeatCapPct, request.ClusterCapPct, request.DailyStopPct,
            request.DailyLossCountStop, request.WeeklyStopPct, request.MaxLeverage, request.MinRR,
            request.MaxPositions, request.LadderThresholds);

        var versions = await db.RiskProfiles.Where(p => p.Name == existing.Name).ToListAsync(ct);
        var wasActive = versions.Any(p => p.IsActive);
        if (wasActive)
        {
            // The new version becomes the single active row.
            foreach (var version in versions)
            {
                version.IsActive = false;
            }
        }

        var next = new RiskProfile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = existing.Name,
            Version = versions.Max(p => p.Version) + 1,
            IsActive = wasActive,
            RiskPct = Pct.ToPoints(request.RiskPct),
            HeatCapPct = Pct.ToPoints(request.HeatCapPct),
            ClusterCapPct = Pct.ToPoints(request.ClusterCapPct),
            DailyStopPct = Pct.ToPoints(request.DailyStopPct),
            DailyLossCountStop = request.DailyLossCountStop,
            WeeklyStopPct = Pct.ToPoints(request.WeeklyStopPct),
            MaxLeverage = request.MaxLeverage,
            MinRR = request.MinRR,
            MaxPositions = request.MaxPositions,
            LadderThresholdsJson = SerializeLadder(request.LadderThresholds ?? ParseLadder(existing.LadderThresholdsJson)),
        };
        db.RiskProfiles.Add(next);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(next));
    }

    private static async Task<IResult> Activate(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var target = await db.RiskProfiles.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ApiException.NotFound("risk_profile_not_found", "Risk profile not found.");

        var actives = await db.RiskProfiles.Where(p => p.IsActive && p.Id != id).ToListAsync(ct);
        foreach (var profile in actives)
        {
            profile.IsActive = false;
        }

        target.IsActive = true;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(target));
    }

    private static async Task<IResult> Delete(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var target = await db.RiskProfiles.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ApiException.NotFound("risk_profile_not_found", "Risk profile not found.");

        var versions = await db.RiskProfiles.Where(p => p.Name == target.Name).ToListAsync(ct);
        if (versions.Any(p => p.IsActive))
        {
            throw ApiException.Conflict(
                "risk_profile_active", "The active risk profile cannot be deleted; activate another profile first.");
        }

        var versionIds = versions.Select(p => p.Id).ToList();
        if (await db.TradePlans.AnyAsync(p => versionIds.Contains(p.RiskProfileId), ct))
        {
            throw ApiException.Conflict(
                "risk_profile_in_use", "Trade plans reference this risk profile; it cannot be deleted.");
        }

        db.RiskProfiles.RemoveRange(versions);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ helpers

    internal static RiskProfileDto ToDto(RiskProfile p) => new(
        p.Id,
        p.Name,
        p.Version,
        p.IsActive,
        Pct.ToFraction(p.RiskPct),
        Pct.ToFraction(p.HeatCapPct),
        Pct.ToFraction(p.ClusterCapPct),
        Pct.ToFraction(p.DailyStopPct),
        p.DailyLossCountStop,
        Pct.ToFraction(p.WeeklyStopPct),
        p.MaxLeverage,
        p.MinRR,
        p.MaxPositions,
        ParseLadder(p.LadderThresholdsJson));

    private static void ValidateFields(
        decimal riskPct, decimal heatCapPct, decimal clusterCapPct, decimal dailyStopPct,
        int dailyLossCountStop, decimal weeklyStopPct, decimal maxLeverage, decimal minRR,
        int maxPositions, LadderThresholdsDto? ladder)
    {
        RequireFraction(riskPct, "riskPct");
        RequireFraction(heatCapPct, "heatCapPct");
        RequireFraction(clusterCapPct, "clusterCapPct");
        RequireFraction(dailyStopPct, "dailyStopPct");
        RequireFraction(weeklyStopPct, "weeklyStopPct");

        if (dailyLossCountStop is < 0 or > 50)
        {
            throw ApiException.BadRequest("validation_error", "dailyLossCountStop must be between 0 and 50.");
        }

        if (maxLeverage is <= 0m or > 125m)
        {
            throw ApiException.BadRequest("validation_error", "maxLeverage must be between 0 and 125.");
        }

        if (minRR is <= 0m or > 50m)
        {
            throw ApiException.BadRequest("validation_error", "minRR must be between 0 and 50.");
        }

        if (maxPositions is < 1 or > 100)
        {
            throw ApiException.BadRequest("validation_error", "maxPositions must be between 1 and 100.");
        }

        if (ladder is not null)
        {
            RequireFraction(ladder.RiskHalvedPct, "ladderThresholds.riskHalvedPct");
            RequireFraction(ladder.PausedPct, "ladderThresholds.pausedPct");
            RequireFraction(ladder.PaperPct, "ladderThresholds.paperPct");
            if (!(ladder.RiskHalvedPct < ladder.PausedPct && ladder.PausedPct < ladder.PaperPct))
            {
                throw ApiException.BadRequest(
                    "validation_error", "ladderThresholds must satisfy riskHalvedPct < pausedPct < paperPct.");
            }
        }
    }

    private static void RequireFraction(decimal value, string field)
    {
        if (value is <= 0m or > 1m)
        {
            throw ApiException.BadRequest(
                "validation_error", $"{field} must be a fraction greater than 0 and at most 1 (0.01 = 1%).");
        }
    }

    /// <summary>Camel-cased object shape read back by PortfolioService.GetLadderThresholdsAsync.</summary>
    private static string SerializeLadder(LadderThresholdsDto ladder) =>
        JsonSerializer.Serialize(
            new { riskHalvedPct = ladder.RiskHalvedPct, pausedPct = ladder.PausedPct, paperPct = ladder.PaperPct });

    /// <summary>
    /// Mirrors PortfolioService.GetLadderThresholdsAsync: an object with fraction values
    /// (percent points tolerated); anything else — including the shipped R-multiple
    /// ladder array — falls back to the 5/10/15% defaults.
    /// </summary>
    public static LadderThresholdsDto ParseLadder(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return PortfolioService.DefaultLadderThresholds;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return PortfolioService.DefaultLadderThresholds;
            }

            return new LadderThresholdsDto(
                ReadThreshold(doc.RootElement, "riskHalvedPct") ?? PortfolioService.DefaultLadderThresholds.RiskHalvedPct,
                ReadThreshold(doc.RootElement, "pausedPct") ?? PortfolioService.DefaultLadderThresholds.PausedPct,
                ReadThreshold(doc.RootElement, "paperPct") ?? PortfolioService.DefaultLadderThresholds.PaperPct);
        }
        catch (JsonException)
        {
            return PortfolioService.DefaultLadderThresholds;
        }
    }

    private static decimal? ReadThreshold(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var value = el.GetDecimal();
        return value > 1m ? value / 100m : value; // tolerate percent points
    }
}
