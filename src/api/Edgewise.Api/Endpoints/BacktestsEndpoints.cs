using System.Text.Json;
using Edgewise.Api.Services.Lab;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Endpoints;

public sealed record CostModelInput(
    string? Venue = null,
    decimal? CommissionPctPerSide = null,
    decimal? SpreadPct = null,
    decimal? SlippagePct = null,
    decimal? FundingPctPer8h = null);

public sealed record RiskModelInput(decimal? RiskPctPerTrade = null, long? EquityStartMinor = null);

public sealed record CreateBacktestRequest(
    Guid StrategyId,
    Guid InstrumentId,
    Timeframe Timeframe,
    DateTime From,
    DateTime To,
    CostModelInput? CostModel = null,
    RiskModelInput? RiskModel = null);

public sealed record BacktestDto(
    Guid Id,
    Guid StrategyId,
    Guid StrategyVersionId,
    int StrategyVersion,
    Guid InstrumentId,
    Timeframe Timeframe,
    DateTime RangeStart,
    DateTime RangeEnd,
    BacktestStatus Status,
    DateTime CreatedAt,
    BacktestConfig? Config,
    JsonElement? Result,
    JsonElement? Honesty,
    string? Error);

public sealed record VenuePresetDto(
    string Venue,
    decimal CommissionPctPerSide,
    decimal SpreadPct,
    decimal SlippagePct,
    decimal FundingPctPer8h);

/// <summary>
/// Backtest orchestration: POST creates a Pending row and enqueues the run on
/// Hangfire (or runs it inline when Hangfire is disabled, e.g. under test).
/// The heavy lifting lives in <see cref="BacktestRunner"/>.
/// </summary>
public sealed class BacktestsEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backtests").RequireAuthorization();

        group.MapGet("/", List);
        group.MapGet("/presets", Presets);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Create);
    }

    private static IResult Presets() => Results.Ok(BacktestRunner.VenuePresets
        .Select(kv => new VenuePresetDto(kv.Key, kv.Value.Commission, kv.Value.Spread, kv.Value.Slippage, kv.Value.Funding))
        .OrderBy(p => p.Venue)
        .ToList());

    private static async Task<IResult> Create(
        CreateBacktestRequest request,
        EdgewiseDbContext db,
        ICurrentUser currentUser,
        IServiceProvider services,
        CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw ApiException.Unauthorized("unauthorized", "Authentication required.");

        var strategy = await db.Strategies.AsNoTracking()
                .SingleOrDefaultAsync(s => s.Id == request.StrategyId, ct)
            ?? throw ApiException.NotFound("strategy_not_found", "Strategy not found.");

        var version = await db.StrategyVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.StrategyId == strategy.Id && v.Version == strategy.CurrentVersion, ct)
            ?? throw ApiException.NotFound("version_not_found", "The strategy's current version is missing.");

        var instrumentExists = await db.Instruments.AnyAsync(i => i.Id == request.InstrumentId, ct);
        if (!instrumentExists)
        {
            throw ApiException.NotFound("instrument_not_found", "The instrument does not exist.");
        }

        var from = AsUtc(request.From);
        var to = AsUtc(request.To);
        if (to <= from)
        {
            throw ApiException.BadRequest("invalid_range", "'to' must be after 'from'.");
        }

        var config = ResolveConfig(request.CostModel, request.RiskModel);

        var backtest = new Backtest
        {
            Id = Guid.NewGuid(),
            StrategyVersionId = version.Id,
            UserId = userId,
            InstrumentIdsJson = LabJson.Serialize(new[] { request.InstrumentId }),
            Timeframe = request.Timeframe,
            RangeStart = from,
            RangeEnd = to,
            CostModelJson = LabJson.Serialize(config),
            Status = BacktestStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.Backtests.Add(backtest);
        await db.SaveChangesAsync(ct);

        // Hangfire when available; inline (synchronous) when disabled — e.g. integration tests.
        var jobs = services.GetService<IBackgroundJobClient>();
        if (jobs is not null)
        {
            jobs.Enqueue<BacktestRunner>(runner => runner.RunAsync(backtest.Id, CancellationToken.None));
        }
        else
        {
            var runner = services.GetRequiredService<BacktestRunner>();
            await runner.RunAsync(backtest.Id, ct);
        }

        return Results.Created($"/api/backtests/{backtest.Id}", await ToDtoAsync(db, backtest.Id, ct));
    }

    private static async Task<IResult> Get(Guid id, EdgewiseDbContext db, CancellationToken ct)
    {
        var dto = await ToDtoAsync(db, id, ct)
            ?? throw ApiException.NotFound("backtest_not_found", "Backtest not found.");
        return Results.Ok(dto);
    }

    private static async Task<IResult> List(EdgewiseDbContext db, Guid? strategyId, CancellationToken ct)
    {
        var query = db.Backtests.AsNoTracking();
        if (strategyId is Guid sid)
        {
            query = query.Where(b => db.StrategyVersions
                .Where(v => v.StrategyId == sid)
                .Select(v => v.Id)
                .Contains(b.StrategyVersionId));
        }

        var rows = await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
        var versionMap = await VersionMapAsync(db, rows.Select(b => b.StrategyVersionId), ct);

        // List view omits the heavy result/honesty payloads.
        return Results.Ok(rows.Select(b => ToDto(b, versionMap, includePayloads: false)).ToList());
    }

    // ------------------------------------------------------------- helpers

    internal static BacktestConfig ResolveConfig(CostModelInput? costs, RiskModelInput? risk)
    {
        var venue = costs?.Venue?.Trim().ToLowerInvariant() ?? "custom";

        decimal commission, spread, slippage, funding;
        if (BacktestRunner.VenuePresets.TryGetValue(venue, out var preset))
        {
            // Preset with per-field overrides (the UI always shows the editable values).
            commission = costs?.CommissionPctPerSide ?? preset.Commission;
            spread = costs?.SpreadPct ?? preset.Spread;
            slippage = costs?.SlippagePct ?? preset.Slippage;
            funding = costs?.FundingPctPer8h ?? preset.Funding;
        }
        else if (venue is "custom")
        {
            commission = costs?.CommissionPctPerSide ?? 0m;
            spread = costs?.SpreadPct ?? 0m;
            slippage = costs?.SlippagePct ?? 0m;
            funding = costs?.FundingPctPer8h ?? 0m;
        }
        else
        {
            throw ApiException.BadRequest(
                "unknown_venue",
                $"Unknown cost venue '{venue}'. Use one of: {string.Join(", ", BacktestRunner.VenuePresets.Keys)}, custom.");
        }

        if (commission < 0m || spread < 0m || slippage < 0m || funding < 0m)
        {
            throw ApiException.BadRequest("invalid_cost_model", "Cost components cannot be negative.");
        }

        if (commission == 0m && spread == 0m && slippage == 0m && funding == 0m)
        {
            throw ApiException.BadRequest(
                "zero_cost_model",
                "An honest backtest requires at least one non-zero cost component.");
        }

        var riskPct = risk?.RiskPctPerTrade ?? 0m; // 0 = "use the rule tree's risk.riskPctPerTrade"
        if (riskPct is < 0m or > 100m)
        {
            throw ApiException.BadRequest("invalid_risk", "riskPctPerTrade must be in (0, 100].");
        }

        var equity = risk?.EquityStartMinor ?? 1_000_000L;
        if (equity <= 0)
        {
            throw ApiException.BadRequest("invalid_equity", "equityStartMinor must be greater than 0.");
        }

        return new BacktestConfig(venue, commission, spread, slippage, funding, riskPct, equity);
    }

    private static async Task<BacktestDto?> ToDtoAsync(EdgewiseDbContext db, Guid id, CancellationToken ct)
    {
        var backtest = await db.Backtests.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct);
        if (backtest is null)
        {
            return null;
        }

        var versionMap = await VersionMapAsync(db, [backtest.StrategyVersionId], ct);
        return ToDto(backtest, versionMap, includePayloads: true);
    }

    private static async Task<Dictionary<Guid, (Guid StrategyId, int Version)>> VersionMapAsync(
        EdgewiseDbContext db, IEnumerable<Guid> versionIds, CancellationToken ct)
    {
        var ids = versionIds.Distinct().ToList();
        return await db.StrategyVersions.AsNoTracking()
            .Where(v => ids.Contains(v.Id))
            .Select(v => new { v.Id, v.StrategyId, v.Version })
            .ToDictionaryAsync(v => v.Id, v => (v.StrategyId, v.Version), ct);
    }

    private static BacktestDto ToDto(
        Backtest b, IReadOnlyDictionary<Guid, (Guid StrategyId, int Version)> versionMap, bool includePayloads)
    {
        var (strategyId, version) = versionMap.TryGetValue(b.StrategyVersionId, out var v) ? v : (Guid.Empty, 0);
        var instrumentIds = LabJson.Deserialize<List<Guid>>(b.InstrumentIdsJson) ?? [];

        string? error = null;
        JsonElement? result = null;
        if (b.ResultJson is not null)
        {
            var parsed = LabJson.ParseOrNull(b.ResultJson);
            if (b.Status == BacktestStatus.Failed)
            {
                error = parsed is JsonElement failed && failed.TryGetProperty("error", out var e)
                    ? e.GetString()
                    : "Backtest failed.";
            }
            else if (includePayloads)
            {
                result = parsed;
            }
        }

        return new BacktestDto(
            b.Id,
            strategyId,
            b.StrategyVersionId,
            version,
            instrumentIds.FirstOrDefault(),
            b.Timeframe,
            b.RangeStart,
            b.RangeEnd,
            b.Status,
            b.CreatedAt,
            b.CostModelJson is null ? null : LabJson.Deserialize<BacktestConfig>(b.CostModelJson),
            result,
            includePayloads ? LabJson.ParseOrNull(b.HonestyJson) : null,
            error);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
