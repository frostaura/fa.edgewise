namespace Edgewise.Domain.Engines.Analytics;

/// <summary>Pure analytics functions over closed-trade statistics.</summary>
public static class RAnalyticsEngine
{
    /// <summary>Groups below this size are flagged as low-sample.</summary>
    public const int LowSampleThreshold = 30;

    /// <summary>Trades entered within this many minutes after a loss count as "after loss" for tilt analysis.</summary>
    public const int TiltWindowMinutes = 60;

    /// <summary>Minimum after-loss sample size before tilt significance is evaluated.</summary>
    public const int TiltMinSample = 10;

    private const double Z95 = 1.959963984540054;

    /// <summary>
    /// Expectancy per group. Win rate and Wilson interval are computed over all trades in the
    /// group (by <see cref="TradeStat.IsWin"/>); R averages use trades with a realised R.
    /// ExpectancyR = winRate * avgWinR + (1 - winRate) * avgLossR.
    /// Groups appear in order of first occurrence.
    /// </summary>
    public static ExpectancyReport<TKey> GroupedExpectancy<TKey>(
        IEnumerable<TradeStat> trades,
        Func<TradeStat, TKey> groupBy)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(groupBy);

        var groups = new List<ExpectancyGroup<TKey>>();
        foreach (var group in trades.GroupBy(groupBy))
        {
            var items = group.ToList();
            var n = items.Count;
            var wins = items.Count(t => t.IsWin);
            var winRate = n == 0 ? 0m : (decimal)wins / n;

            var winRs = items.Where(t => t.IsWin && t.RRealised is not null).Select(t => t.RRealised!.Value).ToList();
            var lossRs = items.Where(t => !t.IsWin && t.RRealised is not null).Select(t => t.RRealised!.Value).ToList();
            var avgWinR = winRs.Count == 0 ? 0m : winRs.Average();
            var avgLossR = lossRs.Count == 0 ? 0m : lossRs.Average();
            var expectancy = winRate * avgWinR + (1m - winRate) * avgLossR;

            var (lo, hi) = Wilson95(wins, n);
            groups.Add(new ExpectancyGroup<TKey>(
                group.Key, n, winRate, avgWinR, avgLossR, expectancy, lo, hi,
                LowSample: n < LowSampleThreshold));
        }

        return new ExpectancyReport<TKey>(groups);
    }

    /// <summary>Wilson score 95% confidence interval for a binomial proportion, clamped to [0, 1].</summary>
    public static (double Lo, double Hi) Wilson95(int successes, int n)
    {
        if (n <= 0)
        {
            return (0d, 0d);
        }

        var p = (double)successes / n;
        var z2 = Z95 * Z95;
        var denom = 1d + z2 / n;
        var centre = (p + z2 / (2d * n)) / denom;
        var half = Z95 * Math.Sqrt(p * (1d - p) / n + z2 / (4d * n * n)) / denom;
        return (Math.Max(0d, centre - half), Math.Min(1d, centre + half));
    }

    /// <summary>
    /// Histogram of realised R multiples. Bins cover [From, To) with From = floor(r / binSize) * binSize.
    /// The bin range is contiguous from the smallest to the largest occupied bin (gaps have Count 0).
    /// Trades without a realised R are ignored.
    /// </summary>
    public static RDistributionReport RDistribution(IEnumerable<TradeStat> trades, decimal binSize = 0.5m)
    {
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(binSize);

        var rs = trades.Where(t => t.RRealised is not null).Select(t => t.RRealised!.Value).ToList();
        if (rs.Count == 0)
        {
            return new RDistributionReport(binSize, [], 0);
        }

        var counts = new Dictionary<int, int>();
        foreach (var r in rs)
        {
            var index = (int)Math.Floor(r / binSize);
            counts[index] = counts.GetValueOrDefault(index) + 1;
        }

        var minIndex = counts.Keys.Min();
        var maxIndex = counts.Keys.Max();
        var bins = new List<RBin>();
        for (var i = minIndex; i <= maxIndex; i++)
        {
            bins.Add(new RBin(i * binSize, (i + 1) * binSize, counts.GetValueOrDefault(i)));
        }

        return new RDistributionReport(binSize, bins, rs.Count);
    }

    /// <summary>
    /// Annotates an equity series (ordered by time; unordered input is sorted) with running peak
    /// and drawdown, and reports max and current drawdown in minor units.
    /// </summary>
    public static DrawdownReport DrawdownCurve(IEnumerable<EquityPoint> equityPoints)
    {
        ArgumentNullException.ThrowIfNull(equityPoints);

        var ordered = equityPoints.OrderBy(p => p.At).ToList();
        var series = new List<DrawdownPoint>(ordered.Count);
        long peak = long.MinValue;
        long maxDrawdown = 0;
        long currentDrawdown = 0;

        foreach (var point in ordered)
        {
            peak = Math.Max(peak, point.EquityMinor);
            currentDrawdown = peak - point.EquityMinor;
            maxDrawdown = Math.Max(maxDrawdown, currentDrawdown);
            series.Add(new DrawdownPoint(point.At, point.EquityMinor, peak, currentDrawdown));
        }

        return new DrawdownReport(series, maxDrawdown, series.Count == 0 ? 0 : currentDrawdown);
    }

    /// <summary>Plan-then-trade ratio per week (weeks start Monday, keyed by ClosedAt), ordered by week.</summary>
    public static IReadOnlyList<WeeklyPlanRate> PtrTrend(IEnumerable<TradeStat> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        return trades
            .GroupBy(t => WeekStartOf(DateOnly.FromDateTime(t.ClosedAt)))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var n = g.Count();
                var planned = g.Count(t => t.HadPlan);
                return new WeeklyPlanRate(g.Key, n, planned, n == 0 ? 0m : (decimal)planned / n);
            })
            .ToList();
    }

    /// <summary>Average adherence score per week over trades that have a score, ordered by week.</summary>
    public static IReadOnlyList<WeeklyAdherence> AdherenceTrend(IEnumerable<TradeStat> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        return trades
            .Where(t => t.AdherenceScore is not null)
            .GroupBy(t => WeekStartOf(DateOnly.FromDateTime(t.ClosedAt)))
            .OrderBy(g => g.Key)
            .Select(g => new WeeklyAdherence(
                g.Key,
                g.Count(),
                (decimal)g.Average(t => (double)t.AdherenceScore!.Value)))
            .ToList();
    }

    /// <summary>Per-calendar-date aggregates (keyed by ClosedAt date), ordered by date.</summary>
    public static IReadOnlyList<CalendarCell> CalendarHeatmap(IEnumerable<TradeStat> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        return trades
            .GroupBy(t => DateOnly.FromDateTime(t.ClosedAt))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var scored = g.Where(t => t.AdherenceScore is not null).ToList();
                return new CalendarCell(
                    g.Key,
                    g.Sum(t => t.RRealised ?? 0m),
                    scored.Count == 0 ? null : (decimal)scored.Average(t => (double)t.AdherenceScore!.Value),
                    g.Count());
            })
            .ToList();
    }

    /// <summary>Per hour-of-day aggregates (user timezone applied by caller), ordered by hour. Only occupied hours are returned.</summary>
    public static IReadOnlyList<HourBucket> SessionClock(IEnumerable<TradeStat> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        return trades
            .GroupBy(t => t.HourOfDay)
            .OrderBy(g => g.Key)
            .Select(g => new HourBucket(g.Key, g.Count(), g.Sum(t => t.RRealised ?? 0m)))
            .ToList();
    }

    /// <summary>
    /// Tilt signature. A trade is "after loss" when its entry (ClosedAt minus HoldingSeconds)
    /// falls within <see cref="TiltWindowMinutes"/> after the close of any earlier losing trade.
    /// Expectancies are the mean realised R of each set; significance uses a Welch t-test
    /// approximation (two-sided, p &lt; 0.05) and requires at least <see cref="TiltMinSample"/>
    /// after-loss trades with realised R.
    /// </summary>
    public static TiltSignatureReport TiltSignature(IEnumerable<TradeStat> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var ordered = trades.OrderBy(t => t.ClosedAt).ToList();
        var lossCloses = ordered.Where(t => !t.IsWin).Select(t => t.ClosedAt).ToList();

        var afterLossR = new List<double>();
        var baselineR = new List<double>();
        foreach (var trade in ordered)
        {
            if (trade.RRealised is not decimal r)
            {
                continue;
            }

            var entryAt = trade.ClosedAt.AddSeconds(-(trade.HoldingSeconds ?? 0));
            var afterLoss = lossCloses.Any(lossClose =>
                lossClose <= entryAt && (entryAt - lossClose).TotalMinutes < TiltWindowMinutes);
            (afterLoss ? afterLossR : baselineR).Add((double)r);
        }

        var expectancyAfterLoss = afterLossR.Count == 0 ? 0m : (decimal)afterLossR.Average();
        var baselineExpectancy = baselineR.Count == 0 ? 0m : (decimal)baselineR.Average();
        var significant = afterLossR.Count >= TiltMinSample && WelchSignificant(afterLossR, baselineR);

        return new TiltSignatureReport(
            afterLossR.Count,
            expectancyAfterLoss,
            baselineExpectancy,
            expectancyAfterLoss - baselineExpectancy,
            significant);
    }

    /// <summary>Monday of the ISO week containing <paramref name="date"/>.</summary>
    public static DateOnly WeekStartOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static bool WelchSignificant(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        int n1 = a.Count, n2 = b.Count;
        if (n1 < 2 || n2 < 2)
        {
            return false;
        }

        var m1 = a.Average();
        var m2 = b.Average();
        var v1 = SampleVariance(a, m1);
        var v2 = SampleVariance(b, m2);
        var se2 = v1 / n1 + v2 / n2;
        if (se2 <= 0d)
        {
            // Both samples are constant: any difference in means is trivially significant.
            return m1 != m2;
        }

        var t = (m1 - m2) / Math.Sqrt(se2);
        var df = se2 * se2 /
                 (v1 * v1 / ((double)n1 * n1 * (n1 - 1)) + v2 * v2 / ((double)n2 * n2 * (n2 - 1)));
        return Math.Abs(t) > TCritical975(df);
    }

    private static double SampleVariance(IReadOnlyList<double> xs, double mean) =>
        xs.Count < 2 ? 0d : xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1);

    // Two-sided 5% critical values of Student's t (0.975 quantile); table for df <= 30,
    // asymptotic approximation beyond.
    private static readonly double[] TTable =
    [
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    ];

    private static double TCritical975(double df)
    {
        var d = Math.Max(1, (int)Math.Floor(df));
        return d <= TTable.Length ? TTable[d - 1] : 1.96 + 2.4 / d;
    }
}
