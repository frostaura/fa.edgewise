namespace Edgewise.Domain.Engines.Forecasting;

/// <summary>One asset's forecast inputs. Values are in minor currency units (e.g. cents).</summary>
/// <param name="Key">Stable asset key, referenced by <see cref="ContributionPlan.ContributionSplit"/>.</param>
/// <param name="CurrentValueMinor">Current value in minor units.</param>
/// <param name="AnnualGrowthPct">Expected annual growth in percent (e.g. 5 = 5%).</param>
/// <param name="AnnualVolPct">Annual volatility in percent; null means 0 (no uncertainty).</param>
public sealed record AssetAssumption(
    string Key,
    long CurrentValueMinor,
    decimal AnnualGrowthPct,
    decimal? AnnualVolPct = null);

/// <summary>
/// Annual contribution plan. <paramref name="AmountMinor"/> is the year-1 annual amount,
/// growing by <paramref name="AnnualIncreasePct"/> each subsequent year, applied in 12 equal
/// monthly installments at month end, split across assets by <paramref name="ContributionSplit"/>
/// (weights are normalized; keys missing from the split receive nothing).
/// </summary>
public sealed record ContributionPlan(
    long AmountMinor,
    decimal AnnualIncreasePct,
    IReadOnlyDictionary<string, decimal> ContributionSplit);

/// <summary>
/// Full forecast assumptions.
/// <paramref name="ReinvestDividends"/> is carried for API completeness: growth rates are
/// interpreted as total return when true; v1 has no separate dividend-yield input, so the
/// flag does not alter the arithmetic yet.
/// <paramref name="HaircutPct"/> is subtracted from every asset's annual growth (a
/// conservatism margin), applied before the vol spread.
/// </summary>
public sealed record Assumptions(
    IReadOnlyList<AssetAssumption> Assets,
    ContributionPlan Contribution,
    bool ReinvestDividends,
    int HorizonYears,
    decimal HaircutPct);

/// <summary>End-of-year portfolio totals (minor units) for the three deterministic scenarios.</summary>
public sealed record DeterministicYearBand(int Year, decimal BearMinor, decimal BaseMinor, decimal BullMinor);

/// <summary>End-of-year portfolio percentile bands (minor units) across Monte Carlo paths.</summary>
public sealed record MonteCarloYearBand(
    int Year, decimal P5Minor, decimal P25Minor, decimal P50Minor, decimal P75Minor, decimal P95Minor);

/// <summary>
/// Deterministic and Monte Carlo wealth projection. Pure and reproducible: the Monte Carlo
/// run is fully determined by (assumptions, paths, seed).
///
/// Deterministic band formula (documented per spec): with g = AnnualGrowthPct - HaircutPct
/// and s = AnnualVolPct (0 when null), the three scenarios compound monthly at the rate
/// (1 + r/100)^(1/12) - 1 where r is:
///   bear = g - s,   base = g,   bull = g + s
/// i.e. bear/bull are the base growth shifted down/up by one annual standard deviation.
///
/// Monte Carlo: per-asset GBM with monthly steps, dt = 1/12. Using fractional rates
/// g = (AnnualGrowthPct - HaircutPct)/100 and s = AnnualVolPct/100, each month the value is
/// multiplied by exp((g - s^2/2)*dt + s*sqrt(dt)*Z) with Z drawn via Box-Muller from a
/// seeded System.Random; contributions are then added at month end. Draw order is fixed
/// (path, then month, then asset in input order), so a given seed always reproduces the
/// same bands.
/// </summary>
public static class ForecastEngine
{
    public const int MinHorizonYears = 1;
    public const int MaxHorizonYears = 20;

    public static IReadOnlyList<DeterministicYearBand> Deterministic(Assumptions assumptions)
    {
        Validate(assumptions);
        var years = assumptions.HorizonYears;
        var bear = RunDeterministic(assumptions, -1m);
        var baseline = RunDeterministic(assumptions, 0m);
        var bull = RunDeterministic(assumptions, +1m);
        var outp = new List<DeterministicYearBand>(years);
        for (var y = 0; y < years; y++)
        {
            outp.Add(new DeterministicYearBand(y + 1, bear[y], baseline[y], bull[y]));
        }

        return outp;
    }

    public static IReadOnlyList<MonteCarloYearBand> MonteCarlo(Assumptions assumptions, int paths = 1000, int seed = 0)
    {
        Validate(assumptions);
        if (paths <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(paths), paths, "paths must be positive.");
        }

        var years = assumptions.HorizonYears;
        var months = years * 12;
        var assets = assumptions.Assets;
        var weights = NormalizedWeights(assumptions);
        var rnd = new Random(seed);

        // yearTotals[y] holds the end-of-year portfolio total for every path.
        var yearTotals = new double[years][];
        for (var y = 0; y < years; y++)
        {
            yearTotals[y] = new double[paths];
        }

        var drift = new double[assets.Count];
        var sigma = new double[assets.Count];
        var sqrtDt = Math.Sqrt(1.0 / 12.0);
        const double dt = 1.0 / 12.0;
        for (var a = 0; a < assets.Count; a++)
        {
            var g = (double)(assets[a].AnnualGrowthPct - assumptions.HaircutPct) / 100.0;
            var s = (double)(assets[a].AnnualVolPct ?? 0m) / 100.0;
            drift[a] = (g - (s * s / 2.0)) * dt;
            sigma[a] = s * sqrtDt;
        }

        var values = new double[assets.Count];
        for (var p = 0; p < paths; p++)
        {
            for (var a = 0; a < assets.Count; a++)
            {
                values[a] = assets[a].CurrentValueMinor;
            }

            for (var m = 1; m <= months; m++)
            {
                var yearIdx = (m - 1) / 12;
                var annualContribution = (double)assumptions.Contribution.AmountMinor
                    * Math.Pow(1.0 + ((double)assumptions.Contribution.AnnualIncreasePct / 100.0), yearIdx);
                var monthlyContribution = annualContribution / 12.0;
                for (var a = 0; a < assets.Count; a++)
                {
                    var z = NextGaussian(rnd);
                    values[a] *= Math.Exp(drift[a] + (sigma[a] * z));
                    values[a] += monthlyContribution * (double)weights[a];
                }

                if (m % 12 == 0)
                {
                    double total = 0;
                    for (var a = 0; a < assets.Count; a++)
                    {
                        total += values[a];
                    }

                    yearTotals[(m / 12) - 1][p] = total;
                }
            }
        }

        var outp = new List<MonteCarloYearBand>(years);
        for (var y = 0; y < years; y++)
        {
            var sorted = yearTotals[y];
            Array.Sort(sorted);
            outp.Add(new MonteCarloYearBand(
                y + 1,
                Percentile(sorted, 5),
                Percentile(sorted, 25),
                Percentile(sorted, 50),
                Percentile(sorted, 75),
                Percentile(sorted, 95)));
        }

        return outp;
    }

    private static decimal[] RunDeterministic(Assumptions assumptions, decimal sigmaShift)
    {
        var assets = assumptions.Assets;
        var weights = NormalizedWeights(assumptions);
        var years = assumptions.HorizonYears;
        var monthlyRates = new decimal[assets.Count];
        for (var a = 0; a < assets.Count; a++)
        {
            var annualPct = assets[a].AnnualGrowthPct - assumptions.HaircutPct
                + (sigmaShift * (assets[a].AnnualVolPct ?? 0m));
            monthlyRates[a] = MonthlyRate(annualPct);
        }

        var values = new decimal[assets.Count];
        for (var a = 0; a < assets.Count; a++)
        {
            values[a] = assets[a].CurrentValueMinor;
        }

        var outp = new decimal[years];
        for (var m = 1; m <= years * 12; m++)
        {
            var yearIdx = (m - 1) / 12;
            var annualContribution = assumptions.Contribution.AmountMinor
                * Pow(1m + (assumptions.Contribution.AnnualIncreasePct / 100m), yearIdx);
            var monthlyContribution = annualContribution / 12m;
            for (var a = 0; a < assets.Count; a++)
            {
                values[a] = (values[a] * (1m + monthlyRates[a])) + (monthlyContribution * weights[a]);
            }

            if (m % 12 == 0)
            {
                decimal total = 0m;
                for (var a = 0; a < assets.Count; a++)
                {
                    total += values[a];
                }

                outp[(m / 12) - 1] = total;
            }
        }

        return outp;
    }

    private static decimal[] NormalizedWeights(Assumptions assumptions)
    {
        var assets = assumptions.Assets;
        var raw = new decimal[assets.Count];
        decimal sum = 0m;
        for (var a = 0; a < assets.Count; a++)
        {
            if (assumptions.Contribution.ContributionSplit.TryGetValue(assets[a].Key, out var w))
            {
                if (w < 0m)
                {
                    throw new ArgumentException($"Contribution split weight for '{assets[a].Key}' is negative.");
                }

                raw[a] = w;
                sum += w;
            }
        }

        if (sum > 0m)
        {
            for (var a = 0; a < raw.Length; a++)
            {
                raw[a] /= sum;
            }
        }

        return raw;
    }

    /// <summary>(1 + annualPct/100)^(1/12) - 1, exact 0 for a 0% annual rate.</summary>
    private static decimal MonthlyRate(decimal annualPct)
    {
        if (annualPct == 0m)
        {
            return 0m;
        }

        var annual = 1.0 + ((double)annualPct / 100.0);
        if (annual <= 0.0)
        {
            // Growth of -100% or worse: floor the monthly multiplier at 0 (total loss).
            return -1m;
        }

        return (decimal)(Math.Pow(annual, 1.0 / 12.0) - 1.0);
    }

    private static decimal Pow(decimal x, int n)
    {
        var r = 1m;
        for (var i = 0; i < n; i++)
        {
            r *= x;
        }

        return r;
    }

    /// <summary>Standard-normal draw via Box-Muller; consumes exactly two uniforms per call.</summary>
    private static double NextGaussian(Random rnd)
    {
        var u1 = 1.0 - rnd.NextDouble(); // in (0, 1] so the log is finite
        var u2 = rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Linear-interpolation percentile over an ascending-sorted array.</summary>
    private static decimal Percentile(double[] sorted, double p)
    {
        var rank = p / 100.0 * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        var v = lo == hi
            ? sorted[lo]
            : sorted[lo] + ((rank - lo) * (sorted[hi] - sorted[lo]));
        return (decimal)v;
    }

    private static void Validate(Assumptions assumptions)
    {
        ArgumentNullException.ThrowIfNull(assumptions);
        ArgumentNullException.ThrowIfNull(assumptions.Assets);
        ArgumentNullException.ThrowIfNull(assumptions.Contribution);
        ArgumentNullException.ThrowIfNull(assumptions.Contribution.ContributionSplit);
        if (assumptions.HorizonYears is < MinHorizonYears or > MaxHorizonYears)
        {
            throw new ArgumentOutOfRangeException(
                nameof(assumptions),
                assumptions.HorizonYears,
                $"HorizonYears must be between {MinHorizonYears} and {MaxHorizonYears}.");
        }

        foreach (var asset in assumptions.Assets)
        {
            if (asset.AnnualVolPct is < 0m)
            {
                throw new ArgumentException($"AnnualVolPct for '{asset.Key}' is negative.");
            }

            if (asset.CurrentValueMinor < 0)
            {
                throw new ArgumentException($"CurrentValueMinor for '{asset.Key}' is negative.");
            }
        }

        if (assumptions.Contribution.AmountMinor < 0)
        {
            throw new ArgumentException("Contribution AmountMinor is negative.");
        }
    }
}
