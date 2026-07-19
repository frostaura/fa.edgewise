namespace Edgewise.Domain.Engines.Calibration;

/// <summary>One resolved forecast: the user's probability, the market's probability at the
/// same moment, the realized outcome, and when it resolved.</summary>
public sealed record ForecastResolution(decimal PUser, decimal PMarket, bool Outcome, DateTime ResolvedAt);

/// <summary>One decile bucket of the reliability curve. <paramref name="BucketMid"/> is the
/// bucket's midpoint (0.05, 0.15, ... 0.95).</summary>
public sealed record ReliabilityBucket(decimal BucketMid, int N, decimal AvgP, decimal ActualFreq);

/// <summary>Mean Brier score of forecasts resolved in one calendar month.</summary>
public sealed record MonthlyBrierPoint(int Year, int Month, int N, decimal MeanBrier);

/// <summary>
/// Calibration diagnostics.
/// - MeanBrier / MeanMarketBrier: average Brier score of the user vs the market on the same
///   events (edge = MeanMarketBrier - MeanBrier; positive means the user beats the market).
/// - Reliability: decile buckets [0,0.1), [0.1,0.2), ..., [0.9,1.0] by PUser; only non-empty
///   buckets are returned.
/// - OverconfidenceIndex: count-weighted mean of (AvgP - ActualFreq) over buckets, which
///   algebraically equals mean(PUser) - mean(outcome). Positive = overconfident.
/// - LongshotBias: for forecasts with PUser &lt; 0.15, ActualFreq - AvgP (positive means
///   longshots resolved true more often than predicted); null when no such forecasts exist.
/// - Trend: monthly mean Brier by ResolvedAt calendar month, chronological.
/// </summary>
public sealed record CalibrationReport(
    int N,
    decimal MeanBrier,
    decimal MeanMarketBrier,
    IReadOnlyList<ReliabilityBucket> Reliability,
    decimal OverconfidenceIndex,
    decimal? LongshotBias,
    IReadOnlyList<MonthlyBrierPoint> Trend);

/// <summary>Pure calibration scoring. No I/O, fully deterministic.</summary>
public static class CalibrationEngine
{
    public const decimal LongshotThreshold = 0.15m;

    /// <summary>Brier score: (p - (outcome ? 1 : 0))^2. 0 is perfect, 1 is maximally wrong.</summary>
    public static decimal BrierScore(decimal pUser, bool outcome)
    {
        ValidateProbability(pUser, nameof(pUser));
        var d = pUser - (outcome ? 1m : 0m);
        return d * d;
    }

    public static CalibrationReport BuildReport(IReadOnlyList<ForecastResolution> forecasts)
    {
        ArgumentNullException.ThrowIfNull(forecasts);
        if (forecasts.Count == 0)
        {
            throw new ArgumentException("At least one resolved forecast is required.", nameof(forecasts));
        }

        var n = forecasts.Count;
        decimal sumBrier = 0m, sumMarketBrier = 0m;
        var bucketN = new int[10];
        var bucketSumP = new decimal[10];
        var bucketHits = new int[10];
        decimal longshotSumP = 0m;
        int longshotN = 0, longshotHits = 0;

        foreach (var f in forecasts)
        {
            ValidateProbability(f.PUser, nameof(ForecastResolution.PUser));
            ValidateProbability(f.PMarket, nameof(ForecastResolution.PMarket));
            sumBrier += BrierScore(f.PUser, f.Outcome);
            sumMarketBrier += BrierScore(f.PMarket, f.Outcome);

            var b = Math.Min(9, (int)(f.PUser * 10m));
            bucketN[b]++;
            bucketSumP[b] += f.PUser;
            if (f.Outcome)
            {
                bucketHits[b]++;
            }

            if (f.PUser < LongshotThreshold)
            {
                longshotN++;
                longshotSumP += f.PUser;
                if (f.Outcome)
                {
                    longshotHits++;
                }
            }
        }

        var reliability = new List<ReliabilityBucket>();
        decimal overconfidence = 0m;
        for (var b = 0; b < 10; b++)
        {
            if (bucketN[b] == 0)
            {
                continue;
            }

            var avgP = bucketSumP[b] / bucketN[b];
            var freq = (decimal)bucketHits[b] / bucketN[b];
            reliability.Add(new ReliabilityBucket((b + 0.5m) / 10m, bucketN[b], avgP, freq));
            overconfidence += bucketN[b] * (avgP - freq);
        }

        overconfidence /= n;

        decimal? longshotBias = longshotN == 0
            ? null
            : ((decimal)longshotHits / longshotN) - (longshotSumP / longshotN);

        var trend = forecasts
            .GroupBy(f => (f.ResolvedAt.Year, f.ResolvedAt.Month))
            .OrderBy(g => g.Key.Year)
            .ThenBy(g => g.Key.Month)
            .Select(g => new MonthlyBrierPoint(
                g.Key.Year,
                g.Key.Month,
                g.Count(),
                g.Average(f => BrierScore(f.PUser, f.Outcome))))
            .ToList();

        return new CalibrationReport(
            n,
            sumBrier / n,
            sumMarketBrier / n,
            reliability,
            overconfidence,
            longshotBias,
            trend);
    }

    private static void ValidateProbability(decimal p, string name)
    {
        if (p is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(name, p, "Probability must be in [0, 1].");
        }
    }
}
