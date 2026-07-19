using System.Text.Json;
using Edgewise.Domain.Engines.Forecasting;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Jobs.Portfolio;

namespace Edgewise.Api.Services.Portfolio;

/// <summary>
/// Validates forecast assumptions (fractions on the wire), converts them for
/// <see cref="ForecastEngine"/> (percent points), runs the engine(s) and builds
/// seed assumptions from the live portfolio.
///
/// Haircut semantics: HaircutPct is a RELATIVE conservatism margin (default 0.20
/// = each asset's positive growth is scaled down by 20%); negative growth is left
/// untouched (a haircut never improves an assumption).
/// </summary>
public sealed class ForecastRunner(PortfolioValuationService valuation)
{
    public const decimal DefaultHaircut = 0.20m;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // -------------------------------------------------------------- (de)serialize

    public static ForecastAssumptionsDto ParseAssumptions(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<ForecastAssumptionsDto>(json, Json);
            return dto ?? throw ApiException.BadRequest("invalid_assumptions", "Assumptions are empty.");
        }
        catch (JsonException)
        {
            throw ApiException.BadRequest("invalid_assumptions", "AssumptionsJson is not valid.");
        }
    }

    public static string SerializeAssumptions(ForecastAssumptionsDto dto) => JsonSerializer.Serialize(dto, Json);

    public static ForecastResultDto? ParseResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ForecastResultDto>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SerializeResult(ForecastResultDto dto) => JsonSerializer.Serialize(dto, Json);

    // -------------------------------------------------------------------- validate

    public static ForecastAssumptionsDto Validate(ForecastAssumptionsDto dto)
    {
        if (dto.Assets is null || dto.Assets.Count == 0)
        {
            throw ApiException.BadRequest("invalid_assumptions", "At least one asset assumption is required.");
        }

        if (dto.Contribution is null || dto.Contribution.Splits is null)
        {
            throw ApiException.BadRequest("invalid_assumptions", "A contribution plan (with splits) is required.");
        }

        if (dto.HorizonYears is < ForecastEngine.MinHorizonYears or > ForecastEngine.MaxHorizonYears)
        {
            throw ApiException.BadRequest(
                "invalid_assumptions",
                $"horizonYears must be between {ForecastEngine.MinHorizonYears} and {ForecastEngine.MaxHorizonYears}.");
        }

        foreach (var asset in dto.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Key))
            {
                throw ApiException.BadRequest("invalid_assumptions", "Every asset needs a non-empty key.");
            }

            if (asset.CurrentValueMinor < 0)
            {
                throw ApiException.BadRequest("invalid_assumptions", $"currentValueMinor for '{asset.Key}' is negative.");
            }

            if (asset.AnnualGrowthPct is < -1m or > 2m)
            {
                throw ApiException.BadRequest(
                    "invalid_assumptions", $"annualGrowthPct for '{asset.Key}' must be between -1 (-100%) and 2 (+200%).");
            }

            if (asset.AnnualVolPct is < 0m or > 3m)
            {
                throw ApiException.BadRequest(
                    "invalid_assumptions", $"annualVolPct for '{asset.Key}' must be between 0 and 3 (300%).");
            }
        }

        if (dto.Contribution.AmountMinor < 0)
        {
            throw ApiException.BadRequest("invalid_assumptions", "contribution.amountMinor cannot be negative.");
        }

        if (dto.Contribution.AnnualIncreasePct is < -1m or > 1m)
        {
            throw ApiException.BadRequest(
                "invalid_assumptions", "contribution.annualIncreasePct must be between -1 and 1.");
        }

        if (dto.Contribution.Splits.Values.Any(w => w < 0m))
        {
            throw ApiException.BadRequest("invalid_assumptions", "Contribution split weights cannot be negative.");
        }

        var haircut = dto.HaircutPct ?? DefaultHaircut;
        if (haircut is < 0m or > 1m)
        {
            throw ApiException.BadRequest("invalid_assumptions", "haircutPct must be between 0 and 1.");
        }

        return dto with { HaircutPct = haircut };
    }

    // ------------------------------------------------------------------------ run

    public static ForecastResultDto Run(ForecastAssumptionsDto dto, bool monteCarlo, int paths, int seed)
    {
        dto = Validate(dto);
        if (monteCarlo && paths is < 1 or > 20000)
        {
            throw ApiException.BadRequest("invalid_paths", "paths must be between 1 and 20000.");
        }

        var assumptions = ToEngine(dto);
        var deterministic = ForecastEngine.Deterministic(assumptions)
            .Select(b => new DeterministicBandDto(
                b.Year, ToMinor(b.BearMinor), ToMinor(b.BaseMinor), ToMinor(b.BullMinor)))
            .ToList();

        List<MonteCarloBandDto>? mc = null;
        if (monteCarlo)
        {
            mc = ForecastEngine.MonteCarlo(assumptions, paths, seed)
                .Select(b => new MonteCarloBandDto(
                    b.Year, ToMinor(b.P5Minor), ToMinor(b.P25Minor), ToMinor(b.P50Minor),
                    ToMinor(b.P75Minor), ToMinor(b.P95Minor)))
                .ToList();
        }

        return new ForecastResultDto(
            DateTime.UtcNow, monteCarlo, monteCarlo ? paths : null, monteCarlo ? seed : null, deterministic, mc);
    }

    private static long ToMinor(decimal value) =>
        value >= long.MaxValue ? long.MaxValue : (long)Math.Round(value);

    /// <summary>Fractions → engine percent points, with the relative haircut pre-applied.</summary>
    public static Assumptions ToEngine(ForecastAssumptionsDto dto)
    {
        var haircut = dto.HaircutPct ?? DefaultHaircut;
        var assets = dto.Assets.Select(a =>
        {
            var growth = a.AnnualGrowthPct > 0m ? a.AnnualGrowthPct * (1m - haircut) : a.AnnualGrowthPct;
            return new AssetAssumption(
                a.Key,
                a.CurrentValueMinor,
                Pct.ToPoints(growth),
                a.AnnualVolPct is { } v ? Pct.ToPoints(v) : null);
        }).ToList();

        var contribution = new ContributionPlan(
            dto.Contribution.AmountMinor,
            Pct.ToPoints(dto.Contribution.AnnualIncreasePct),
            dto.Contribution.Splits);

        return new Assumptions(assets, contribution, dto.Reinvest, dto.HorizonYears, HaircutPct: 0m);
    }

    // ----------------------------------------------------------------------- seed

    /// <summary>
    /// Suggested assumptions from current holdings: value from live valuation, growth from
    /// naive CAGR (oldest lot vs current, when at least ~6 months of history exists) capped
    /// to [-10%, +25%], otherwise an asset-class default. Splits follow the buckets'
    /// contribution splits, shared equally between a bucket's holdings.
    /// </summary>
    public async Task<ForecastSeedDto> SeedFromPortfolioAsync(Guid userId, CancellationToken ct)
    {
        var rows = await valuation.ValueHoldingsAsync(userId, ct);
        var buckets = await valuation.ValueBucketsAsync(userId, ct, rows);
        var bucketSplit = buckets.ToDictionary(b => b.Bucket.Id, b => Pct.ToFraction(b.Bucket.ContributionSplitPct));
        var holdingsPerBucket = rows.GroupBy(r => r.BucketId).ToDictionary(g => g.Key, g => g.Count());

        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<ForecastAssetAssumptionDto>();
        var notes = new List<ForecastSeedNoteDto>();
        var splits = new Dictionary<string, decimal>();
        var now = DateTime.UtcNow;

        foreach (var row in rows.Where(r => r.Value.ValueMinor > 0))
        {
            var key = row.Symbol;
            for (var n = 2; !usedKeys.Add(key); n++)
            {
                key = $"{row.Symbol}-{n}";
            }

            decimal growth;
            var capped = false;
            var fromLots = false;
            if (row.ManualGrowthRatePct is { } manual)
            {
                growth = Pct.ToFraction(manual);
            }
            else if (row.OldestLotAt is { } oldest
                && (now - oldest).TotalDays >= 180
                && row.Value.CostBasisMinor > 0)
            {
                var years = (now - oldest).TotalDays / 365.25;
                var cagr = Math.Pow((double)row.Value.ValueMinor / row.Value.CostBasisMinor, 1.0 / years) - 1.0;
                growth = (decimal)cagr;
                fromLots = true;
                if (growth < -0.10m)
                {
                    growth = -0.10m;
                    capped = true;
                }
                else if (growth > 0.25m)
                {
                    growth = 0.25m;
                    capped = true;
                }
            }
            else
            {
                growth = DefaultGrowth(row.AssetClass);
            }

            assets.Add(new ForecastAssetAssumptionDto(
                key, row.Value.ValueMinor, decimal.Round(growth, 4), DefaultVol(row.AssetClass)));
            notes.Add(new ForecastSeedNoteDto(key, row.HoldingId, row.Symbol, capped, fromLots));

            var perBucket = bucketSplit.GetValueOrDefault(row.BucketId);
            var count = holdingsPerBucket.GetValueOrDefault(row.BucketId, 1);
            splits[key] = count > 0 ? decimal.Round(perBucket / count, 6) : 0m;
        }

        if (splits.Count > 0 && splits.Values.All(v => v == 0m))
        {
            foreach (var key in splits.Keys.ToList())
            {
                splits[key] = decimal.Round(1m / splits.Count, 6);
            }
        }

        var assumptions = new ForecastAssumptionsDto(
            assets,
            new ForecastContributionDto(0, 0m, splits),
            Reinvest: true,
            HorizonYears: 10,
            HaircutPct: DefaultHaircut);
        return new ForecastSeedDto(assumptions, notes);
    }

    private static decimal DefaultGrowth(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Crypto => 0.15m,
        AssetClass.Equity => 0.10m,
        AssetClass.Etf => 0.10m,
        AssetClass.Prediction => 0m,
        AssetClass.Cash => 0.05m,
        _ => 0.06m,
    };

    private static decimal? DefaultVol(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Crypto => 0.60m,
        AssetClass.Equity => 0.25m,
        AssetClass.Etf => 0.15m,
        AssetClass.Prediction => 0.50m,
        AssetClass.Cash => 0m,
        _ => 0.10m,
    };
}
