using System.Text.Json;
using Edgewise.Api.Services.Lab;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

public sealed record StrategyVersionDto(int Version, JsonElement RuleTree, DateTime At);

public sealed record LatestBacktestSummaryDto(
    Guid Id,
    BacktestStatus Status,
    DateTime CreatedAt,
    decimal? ExpectancyR,
    int? Trades,
    bool? IsExploratory,
    string? SampleVerdict);

public sealed record StrategyListItemDto(
    Guid Id,
    string Name,
    StrategyState State,
    int CurrentVersion,
    LatestBacktestSummaryDto? LatestBacktest);

public sealed record StrategyDetailDto(
    Guid Id,
    string Name,
    StrategyState State,
    int CurrentVersion,
    JsonElement RuleTree,
    IReadOnlyList<StrategyVersionDto> Versions,
    LatestBacktestSummaryDto? LatestBacktest);

public sealed record CreateStrategyRequest(string Name, JsonElement RuleTree);

public sealed record UpdateStrategyRequest(string? Name, JsonElement? RuleTree);

public sealed record TransitionRequest(StrategyState ToState, bool Force = false, string? Reason = null);

/// <summary>
/// Strategy designer CRUD + versioning, the four playbook templates, and the
/// Draft->Backtested->Paper->TinyLive->Live promotion pipeline.
/// Rule trees are validated on every write against the canonical JSON Schema
/// (see <see cref="RuleTreeValidator"/>); invalid trees get 400 with per-field errors.
/// </summary>
public sealed class StrategiesEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/strategies").RequireAuthorization();

        group.MapGet("/", List);
        group.MapGet("/templates", Templates);
        group.MapPost("/", Create);
        group.MapGet("/{id:guid}", Get);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapPost("/{id:guid}/transition", Transition);
        group.MapGet("/{id:guid}/pipeline", Pipeline);
    }

    private static IResult Templates() => Results.Ok(StrategyTemplates.All);

    private static async Task<IResult> List(EdgewiseDbContext db, CancellationToken ct)
    {
        var strategies = await db.Strategies.AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        var items = new List<StrategyListItemDto>(strategies.Count);
        foreach (var strategy in strategies)
        {
            items.Add(new StrategyListItemDto(
                strategy.Id, strategy.Name, strategy.State, strategy.CurrentVersion,
                await LatestBacktestAsync(db, strategy.Id, ct)));
        }

        return Results.Ok(items);
    }

    private static async Task<IResult> Get(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var strategy = await db.Strategies.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ApiException.NotFound("strategy_not_found", "Strategy not found.");

        var versions = await db.StrategyVersions.AsNoTracking()
            .Where(v => v.StrategyId == id)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);

        var current = versions.FirstOrDefault(v => v.Version == strategy.CurrentVersion)
            ?? throw ApiException.NotFound("version_not_found", "The strategy's current version is missing.");

        return Results.Ok(new StrategyDetailDto(
            strategy.Id,
            strategy.Name,
            strategy.State,
            strategy.CurrentVersion,
            LabJson.ParseOrNull(current.RuleTreeJson)!.Value,
            versions.Select(v => new StrategyVersionDto(v.Version, LabJson.ParseOrNull(v.RuleTreeJson)!.Value, v.At)).ToList(),
            await LatestBacktestAsync(db, id, ct)));
    }

    private static async Task<IResult> Create(
        CreateStrategyRequest request,
        EdgewiseDbContext db,
        RuleTreeValidator validator,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw ApiException.BadRequest("invalid_name", "name is required.");
        }

        if (InvalidRuleTree(validator, request.RuleTree, out var errorResult))
        {
            return errorResult;
        }

        var strategy = new Strategy
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name.Trim(),
            State = StrategyState.Draft,
            CurrentVersion = 1,
        };
        db.Strategies.Add(strategy);
        db.StrategyVersions.Add(new StrategyVersion
        {
            Id = Guid.NewGuid(),
            StrategyId = strategy.Id,
            Version = 1,
            RuleTreeJson = request.RuleTree.GetRawText(),
            At = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/strategies/{strategy.Id}",
            new StrategyListItemDto(strategy.Id, strategy.Name, strategy.State, strategy.CurrentVersion, null));
    }

    private static async Task<IResult> Update(
        Guid id,
        UpdateStrategyRequest request,
        EdgewiseDbContext db,
        RuleTreeValidator validator,
        CancellationToken ct)
    {
        var strategy = await db.Strategies.SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ApiException.NotFound("strategy_not_found", "Strategy not found.");

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                throw ApiException.BadRequest("invalid_name", "name cannot be blank.");
            }

            strategy.Name = request.Name.Trim();
        }

        if (request.RuleTree is JsonElement ruleTree && ruleTree.ValueKind is not JsonValueKind.Undefined)
        {
            if (InvalidRuleTree(validator, ruleTree, out var errorResult))
            {
                return errorResult;
            }

            var current = await db.StrategyVersions
                .SingleOrDefaultAsync(v => v.StrategyId == id && v.Version == strategy.CurrentVersion, ct);

            // Immutable history: identical trees do not create a new version.
            var newJson = ruleTree.GetRawText();
            if (current is null || !JsonEquals(current.RuleTreeJson, newJson))
            {
                strategy.CurrentVersion = (current?.Version ?? 0) + 1;
                db.StrategyVersions.Add(new StrategyVersion
                {
                    Id = Guid.NewGuid(),
                    StrategyId = id,
                    Version = strategy.CurrentVersion,
                    RuleTreeJson = newJson,
                    At = DateTime.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return await Get(id, db, ct);
    }

    private static async Task<IResult> Delete(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var strategy = await db.Strategies.SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ApiException.NotFound("strategy_not_found", "Strategy not found.");

        var versionIds = await db.StrategyVersions
            .Where(v => v.StrategyId == id)
            .Select(v => v.Id)
            .ToListAsync(ct);
        var hasBacktests = await db.Backtests.AnyAsync(b => versionIds.Contains(b.StrategyVersionId), ct);
        var hasTrades = await db.Trades.AnyAsync(t => t.StrategyId == id, ct);
        if (hasBacktests || hasTrades)
        {
            throw ApiException.Conflict(
                "strategy_in_use",
                "This strategy has backtests or trades attached and cannot be deleted. Archive it by moving it back to Draft instead.");
        }

        db.StrategyVersions.RemoveRange(await db.StrategyVersions.Where(v => v.StrategyId == id).ToListAsync(ct));
        db.PipelineStateChanges.RemoveRange(await db.PipelineStateChanges.Where(p => p.StrategyId == id).ToListAsync(ct));
        db.Strategies.Remove(strategy);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Transition(
        Guid id, TransitionRequest request, EdgewiseDbContext db, PipelineService pipeline, CancellationToken ct)
    {
        var strategy = await db.Strategies.SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ApiException.NotFound("strategy_not_found", "Strategy not found.");

        var dto = await pipeline.TransitionAsync(strategy, request.ToState, request.Force, request.Reason, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> Pipeline(
        Guid id, EdgewiseDbContext db, PipelineService pipeline, CancellationToken ct)
    {
        var strategy = await db.Strategies.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw ApiException.NotFound("strategy_not_found", "Strategy not found.");

        return Results.Ok(await pipeline.GetPipelineAsync(strategy, ct));
    }

    // ------------------------------------------------------------- helpers

    /// <summary>400 with {"error":{code,message,fields}} when the rule tree fails validation.</summary>
    private static bool InvalidRuleTree(RuleTreeValidator validator, JsonElement ruleTree, out IResult errorResult)
    {
        if (ruleTree.ValueKind is not JsonValueKind.Object)
        {
            errorResult = Results.Json(new
            {
                error = new
                {
                    code = "invalid_rule_tree",
                    message = "ruleTree must be a JSON object.",
                    fields = new Dictionary<string, string> { ["$"] = "ruleTree must be a JSON object." },
                },
            }, statusCode: StatusCodes.Status400BadRequest);
            return true;
        }

        var fields = validator.Validate(ruleTree);
        if (fields.Count > 0)
        {
            errorResult = Results.Json(new
            {
                error = new
                {
                    code = "invalid_rule_tree",
                    message = "The rule tree failed validation. See fields for details.",
                    fields,
                },
            }, statusCode: StatusCodes.Status400BadRequest);
            return true;
        }

        errorResult = Results.Empty;
        return false;
    }

    private static bool JsonEquals(string a, string b)
    {
        using var docA = JsonDocument.Parse(a);
        using var docB = JsonDocument.Parse(b);
        return JsonElement.DeepEquals(docA.RootElement, docB.RootElement);
    }

    internal static async Task<LatestBacktestSummaryDto?> LatestBacktestAsync(
        EdgewiseDbContext db, Guid strategyId, CancellationToken ct)
    {
        var latest = await db.Backtests.AsNoTracking()
            .Where(b => db.StrategyVersions
                .Where(v => v.StrategyId == strategyId)
                .Select(v => v.Id)
                .Contains(b.StrategyVersionId))
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (latest is null)
        {
            return null;
        }

        decimal? expectancy = null;
        int? trades = null;
        bool? isExploratory = null;
        string? verdict = null;

        if (latest.Status == BacktestStatus.Done && latest.ResultJson is string resultJson)
        {
            using var result = JsonDocument.Parse(resultJson);
            if (result.RootElement.TryGetProperty("expectancyR", out var er))
            {
                expectancy = er.GetDecimal();
            }

            if (result.RootElement.TryGetProperty("n", out var n))
            {
                trades = n.GetInt32();
            }
        }

        if (latest.HonestyJson is string honestyJson)
        {
            using var honesty = JsonDocument.Parse(honestyJson);
            if (honesty.RootElement.TryGetProperty("isExploratory", out var flag))
            {
                isExploratory = flag.GetBoolean();
            }

            if (honesty.RootElement.TryGetProperty("sampleVerdict", out var sv))
            {
                verdict = sv.GetString();
            }
        }

        return new LatestBacktestSummaryDto(
            latest.Id, latest.Status, latest.CreatedAt, expectancy, trades, isExploratory, verdict);
    }
}
