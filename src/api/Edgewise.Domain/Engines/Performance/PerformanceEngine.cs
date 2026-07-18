namespace Edgewise.Domain.Engines.Performance;

/// <summary>End-of-day account equity in minor currency units.</summary>
public sealed record EquitySnapshot(DateOnly Date, long EquityMinor);

/// <summary>Net external flow (deposits positive, withdrawals negative) for a day, minor units. Flows are assumed start-of-day.</summary>
public sealed record NetFlow(DateOnly Date, long NetFlowMinor);

/// <summary>A dated cashflow for money-weighted return (investments negative, proceeds/terminal value positive), minor units.</summary>
public sealed record CashflowItem(DateOnly Date, long AmountMinor);

/// <summary>One sub-period of a time-weighted return series.</summary>
/// <param name="Date">End date of the sub-period (snapshot date).</param>
/// <param name="PeriodReturn">Sub-period return as a fraction (0.10 = 10%).</param>
/// <param name="CumulativeReturn">Geometrically linked cumulative return up to and including this sub-period.</param>
public sealed record TwrPoint(DateOnly Date, decimal PeriodReturn, decimal CumulativeReturn);

/// <summary>Time-weighted return series and total.</summary>
public sealed record TwrResult(IReadOnlyList<TwrPoint> Points, decimal TotalReturn);

/// <summary>
/// Pure performance mathematics: time-weighted return, XIRR (money-weighted return),
/// annualisation and drawdown helpers. Deterministic, no I/O.
/// </summary>
public static class PerformanceEngine
{
    private const double XirrLowerBound = -0.9999;
    private const double XirrUpperBound = 10.0;
    private const double XirrTolerance = 1e-7;
    private const int XirrMaxIterations = 200;

    /// <summary>
    /// Computes the time-weighted return over daily equity snapshots.
    /// Sub-period return between consecutive snapshots: r = (E_end - F) / E_start - 1
    /// where F is the net external flow dated after the start snapshot up to and including
    /// the end snapshot date (flows assumed start-of-day). Sub-periods starting from zero
    /// equity are skipped safely (period return 0, no geometric contribution).
    /// </summary>
    public static TwrResult ComputeTwr(
        IReadOnlyList<EquitySnapshot> dailySnapshots,
        IReadOnlyList<NetFlow> flows)
    {
        ArgumentNullException.ThrowIfNull(dailySnapshots);
        ArgumentNullException.ThrowIfNull(flows);

        // Deduplicate by date (last wins) and sort; aggregate flows per date.
        var snapshots = dailySnapshots
            .GroupBy(s => s.Date)
            .Select(g => g.Last())
            .OrderBy(s => s.Date)
            .ToList();

        var flowByDate = flows
            .GroupBy(f => f.Date)
            .ToDictionary(g => g.Key, g => g.Sum(f => f.NetFlowMinor));

        var points = new List<TwrPoint>();
        if (snapshots.Count < 2)
        {
            return new TwrResult(points, 0m);
        }

        var cumulativeFactor = 1m;
        for (var i = 1; i < snapshots.Count; i++)
        {
            var start = snapshots[i - 1];
            var end = snapshots[i];

            // Flows strictly after the start snapshot date, up to and including the end date.
            var flow = flowByDate
                .Where(kv => kv.Key > start.Date && kv.Key <= end.Date)
                .Sum(kv => kv.Value);

            if (start.EquityMinor == 0)
            {
                // Zero-equity start: return is undefined; skip the period safely.
                points.Add(new TwrPoint(end.Date, 0m, cumulativeFactor - 1m));
                continue;
            }

            var factor = (end.EquityMinor - flow) / (decimal)start.EquityMinor;
            cumulativeFactor *= factor;
            points.Add(new TwrPoint(end.Date, factor - 1m, cumulativeFactor - 1m));
        }

        return new TwrResult(points, cumulativeFactor - 1m);
    }

    /// <summary>
    /// Annualised internal rate of return (XIRR, Actual/365) of dated cashflows.
    /// Investments are negative, proceeds and terminal value positive.
    /// Bisection + Newton hybrid on [-0.9999, 10], tolerance 1e-7, max 200 iterations.
    /// Returns null when a root cannot be bracketed (no sign change) or fewer than two flows.
    /// </summary>
    public static decimal? ComputeXirr(IReadOnlyList<CashflowItem> cashflows)
    {
        if (cashflows is null || cashflows.Count < 2)
        {
            return null;
        }

        var sorted = cashflows.OrderBy(c => c.Date).ToList();
        var anyPositive = sorted.Any(c => c.AmountMinor > 0);
        var anyNegative = sorted.Any(c => c.AmountMinor < 0);
        if (!anyPositive || !anyNegative)
        {
            return null;
        }

        // Normalise amounts so NPV tolerance is scale-independent.
        var scale = (double)sorted.Max(c => Math.Abs(c.AmountMinor));
        var t0 = sorted[0].Date;
        var times = sorted.Select(c => (c.Date.DayNumber - t0.DayNumber) / 365.0).ToArray();
        var amounts = sorted.Select(c => c.AmountMinor / scale).ToArray();

        double Npv(double rate)
        {
            var sum = 0.0;
            for (var i = 0; i < amounts.Length; i++)
            {
                sum += amounts[i] * Math.Pow(1.0 + rate, -times[i]);
            }

            return sum;
        }

        double NpvDerivative(double rate)
        {
            var sum = 0.0;
            for (var i = 0; i < amounts.Length; i++)
            {
                sum += -times[i] * amounts[i] * Math.Pow(1.0 + rate, -times[i] - 1.0);
            }

            return sum;
        }

        var lo = XirrLowerBound;
        var hi = XirrUpperBound;
        var fLo = Npv(lo);
        var fHi = Npv(hi);

        if (double.IsNaN(fLo) || double.IsNaN(fHi))
        {
            return null;
        }

        if (fLo == 0.0)
        {
            return (decimal)lo;
        }

        if (fHi == 0.0)
        {
            return (decimal)hi;
        }

        if (Math.Sign(fLo) == Math.Sign(fHi))
        {
            // No sign change on the bracket: no solvable root in range.
            return null;
        }

        var x = (lo + hi) / 2.0;
        for (var i = 0; i < XirrMaxIterations; i++)
        {
            var fx = Npv(x);
            if (double.IsNaN(fx))
            {
                x = (lo + hi) / 2.0;
                continue;
            }

            if (Math.Abs(fx) < 1e-12)
            {
                break;
            }

            // Maintain the bracket.
            if (Math.Sign(fx) == Math.Sign(fLo))
            {
                lo = x;
                fLo = fx;
            }
            else
            {
                hi = x;
                fHi = fx;
            }

            if (hi - lo < XirrTolerance)
            {
                break;
            }

            // Newton step, falling back to bisection when the step is unusable
            // or would leave the bracket.
            var dfx = NpvDerivative(x);
            var newton = dfx != 0.0 && !double.IsNaN(dfx) && !double.IsInfinity(dfx)
                ? x - (fx / dfx)
                : double.NaN;
            x = double.IsNaN(newton) || newton <= lo || newton >= hi
                ? (lo + hi) / 2.0
                : newton;
        }

        return (decimal)x;
    }

    /// <summary>
    /// Annualises a total return earned over <paramref name="days"/> days (Actual/365,
    /// geometric). Returns null when days is non-positive or the growth factor is non-positive.
    /// </summary>
    public static decimal? AnnualizedReturn(decimal totalReturn, int days)
    {
        if (days <= 0)
        {
            return null;
        }

        var growth = 1m + totalReturn;
        if (growth <= 0m)
        {
            return null;
        }

        return (decimal)(Math.Pow((double)growth, 365.0 / days) - 1.0);
    }

    /// <summary>
    /// Maximum peak-to-trough drawdown of an equity series, as a positive fraction
    /// (0.25 = 25% drawdown). Returns 0 for empty, single-point, or non-decreasing series.
    /// Peaks at or below zero equity are skipped safely.
    /// </summary>
    public static decimal MaxDrawdown(IReadOnlyList<EquitySnapshot> equitySeries)
    {
        ArgumentNullException.ThrowIfNull(equitySeries);

        var maxDrawdown = 0m;
        var peak = long.MinValue;
        foreach (var point in equitySeries.OrderBy(s => s.Date))
        {
            if (point.EquityMinor > peak)
            {
                peak = point.EquityMinor;
            }

            if (peak > 0)
            {
                var drawdown = (peak - point.EquityMinor) / (decimal)peak;
                if (drawdown > maxDrawdown)
                {
                    maxDrawdown = drawdown;
                }
            }
        }

        return maxDrawdown;
    }
}
