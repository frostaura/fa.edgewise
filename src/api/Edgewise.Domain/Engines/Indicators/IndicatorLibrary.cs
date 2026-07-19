namespace Edgewise.Domain.Engines.Indicators;

/// <summary>
/// The single, shared indicator implementation for Edgewise (chart endpoints and the
/// backtester both consume this). Pure, deterministic, no I/O.
///
/// Conventions:
/// - Every function returns arrays aligned 1:1 with the input bars; warmup slots are null.
/// - All indicators are strictly causal: the value at index i depends only on bars[0..i].
/// - Wilder smoothing (RSI, ATR): first value is a simple average of the first
///   <c>period</c> observations, then s[i] = (s[i-1]*(period-1) + x[i]) / period.
/// - EMA: seeded with the SMA of the first <c>period</c> values, multiplier 2/(period+1).
/// - Bollinger uses the population standard deviation (divide by n), the standard
///   TA convention (matches StockCharts / TA-Lib default).
/// </summary>
public static class IndicatorLibrary
{
    /// <summary>Simple moving average of closes. Null for the first period-1 slots.</summary>
    public static decimal?[] Sma(IReadOnlyList<Bar> bars, int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return SmaCore(SelectCloses(bars), period);
    }

    /// <summary>Exponential moving average of closes, SMA-seeded. Null for the first period-1 slots.</summary>
    public static decimal?[] Ema(IReadOnlyList<Bar> bars, int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return EmaCore(ToNullable(SelectCloses(bars)), period);
    }

    /// <summary>
    /// Relative Strength Index with Wilder smoothing. First value at index <c>period</c>
    /// (needs <c>period</c> close-to-close changes). If the average loss is zero the RSI is 100
    /// (Wilder's convention); if both average gain and loss are zero (flat series) it is 50.
    /// </summary>
    public static decimal?[] Rsi(IReadOnlyList<Bar> bars, int period = 14)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ValidatePeriod(period);
        var c = SelectCloses(bars);
        var n = c.Length;
        var outp = new decimal?[n];
        if (n <= period)
        {
            return outp;
        }

        decimal avgGain = 0m, avgLoss = 0m;
        for (var i = 1; i <= period; i++)
        {
            var change = c[i] - c[i - 1];
            if (change > 0)
            {
                avgGain += change;
            }
            else
            {
                avgLoss -= change;
            }
        }

        avgGain /= period;
        avgLoss /= period;
        outp[period] = ToRsi(avgGain, avgLoss);

        for (var i = period + 1; i < n; i++)
        {
            var change = c[i] - c[i - 1];
            var gain = change > 0 ? change : 0m;
            var loss = change < 0 ? -change : 0m;
            avgGain = ((avgGain * (period - 1)) + gain) / period;
            avgLoss = ((avgLoss * (period - 1)) + loss) / period;
            outp[i] = ToRsi(avgGain, avgLoss);
        }

        return outp;
    }

    /// <summary>
    /// MACD(fast, slow, signal): macd = EMA(fast) - EMA(slow) of closes (non-null from index
    /// slow-1), signal = EMA(signalPeriod) of the macd line (non-null from index
    /// slow-1+signalPeriod-1), hist = macd - signal.
    /// </summary>
    public static (decimal?[] Macd, decimal?[] Signal, decimal?[] Hist) Macd(
        IReadOnlyList<Bar> bars, int fast = 12, int slow = 26, int signalPeriod = 9)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ValidatePeriod(fast);
        ValidatePeriod(slow);
        ValidatePeriod(signalPeriod);
        if (fast >= slow)
        {
            throw new ArgumentOutOfRangeException(nameof(fast), "fast period must be < slow period.");
        }

        var closes = ToNullable(SelectCloses(bars));
        var emaFast = EmaCore(closes, fast);
        var emaSlow = EmaCore(closes, slow);
        var n = closes.Length;
        var macd = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            if (emaFast[i] is decimal f && emaSlow[i] is decimal s)
            {
                macd[i] = f - s;
            }
        }

        var signal = EmaCore(macd, signalPeriod);
        var hist = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            if (macd[i] is decimal m && signal[i] is decimal sg)
            {
                hist[i] = m - sg;
            }
        }

        return (macd, signal, hist);
    }

    /// <summary>
    /// Average True Range with Wilder smoothing. TR[0] = H-L (no previous close);
    /// TR[i] = max(H-L, |H-prevC|, |L-prevC|). First ATR at index period-1 is the simple
    /// average of the first <c>period</c> TRs, then Wilder smoothing.
    /// </summary>
    public static decimal?[] Atr(IReadOnlyList<Bar> bars, int period = 14)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ValidatePeriod(period);
        var n = bars.Count;
        var outp = new decimal?[n];
        if (n < period)
        {
            return outp;
        }

        var tr = new decimal[n];
        for (var i = 0; i < n; i++)
        {
            var hl = bars[i].H - bars[i].L;
            if (i == 0)
            {
                tr[i] = hl;
            }
            else
            {
                var prevC = bars[i - 1].C;
                var hc = Math.Abs(bars[i].H - prevC);
                var lc = Math.Abs(bars[i].L - prevC);
                tr[i] = Math.Max(hl, Math.Max(hc, lc));
            }
        }

        decimal sum = 0m;
        for (var i = 0; i < period; i++)
        {
            sum += tr[i];
        }

        var atr = sum / period;
        outp[period - 1] = atr;
        for (var i = period; i < n; i++)
        {
            atr = ((atr * (period - 1)) + tr[i]) / period;
            outp[i] = atr;
        }

        return outp;
    }

    /// <summary>
    /// Bollinger Bands: mid = SMA(period), upper/lower = mid +/- stdDevMult * population
    /// standard deviation of the same window.
    /// </summary>
    public static (decimal?[] Mid, decimal?[] Upper, decimal?[] Lower) Bollinger(
        IReadOnlyList<Bar> bars, int period = 20, decimal stdDevMult = 2m)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ValidatePeriod(period);
        var c = SelectCloses(bars);
        var n = c.Length;
        var mid = SmaCore(c, period);
        var upper = new decimal?[n];
        var lower = new decimal?[n];
        for (var i = period - 1; i < n; i++)
        {
            var mean = mid[i]!.Value;
            decimal sumSq = 0m;
            for (var j = i - period + 1; j <= i; j++)
            {
                var d = c[j] - mean;
                sumSq += d * d;
            }

            var sd = Sqrt(sumSq / period);
            upper[i] = mean + (stdDevMult * sd);
            lower[i] = mean - (stdDevMult * sd);
        }

        return (mid, upper, lower);
    }

    /// <summary>
    /// Session-anchored VWAP: cumulative sum of typical price (H+L+C)/3 times volume over
    /// cumulative volume, reset at every calendar-date change of <see cref="Bar.Ts"/>.
    /// Null while the session's cumulative volume is zero.
    /// </summary>
    public static decimal?[] Vwap(IReadOnlyList<Bar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        var n = bars.Count;
        var outp = new decimal?[n];
        decimal cumPv = 0m, cumV = 0m;
        DateTime? session = null;
        for (var i = 0; i < n; i++)
        {
            var date = bars[i].Ts.Date;
            if (session != date)
            {
                session = date;
                cumPv = 0m;
                cumV = 0m;
            }

            var tp = (bars[i].H + bars[i].L + bars[i].C) / 3m;
            cumPv += tp * bars[i].V;
            cumV += bars[i].V;
            outp[i] = cumV > 0m ? cumPv / cumV : null;
        }

        return outp;
    }

    /// <summary>SMA over an arbitrary decimal series (used for volume averages).</summary>
    public static decimal?[] SmaOf(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        var arr = new decimal[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            arr[i] = values[i];
        }

        return SmaCore(arr, period);
    }

    private static decimal ToRsi(decimal avgGain, decimal avgLoss)
    {
        if (avgLoss == 0m)
        {
            return avgGain == 0m ? 50m : 100m;
        }

        var rs = avgGain / avgLoss;
        return 100m - (100m / (1m + rs));
    }

    private static decimal?[] SmaCore(decimal[] values, int period)
    {
        ValidatePeriod(period);
        var n = values.Length;
        var outp = new decimal?[n];
        decimal sum = 0m;
        for (var i = 0; i < n; i++)
        {
            sum += values[i];
            if (i >= period)
            {
                sum -= values[i - period];
            }

            if (i >= period - 1)
            {
                outp[i] = sum / period;
            }
        }

        return outp;
    }

    /// <summary>
    /// EMA over a possibly-null-prefixed series: skips the leading null run, seeds with the
    /// SMA of the first <c>period</c> non-null values, then applies the standard recursion.
    /// </summary>
    private static decimal?[] EmaCore(decimal?[] values, int period)
    {
        ValidatePeriod(period);
        var n = values.Length;
        var outp = new decimal?[n];
        var start = 0;
        while (start < n && values[start] is null)
        {
            start++;
        }

        if (n - start < period)
        {
            return outp;
        }

        decimal seed = 0m;
        for (var i = start; i < start + period; i++)
        {
            seed += values[i]!.Value;
        }

        var ema = seed / period;
        outp[start + period - 1] = ema;
        var k = 2m / (period + 1);
        for (var i = start + period; i < n; i++)
        {
            ema += k * (values[i]!.Value - ema);
            outp[i] = ema;
        }

        return outp;
    }

    private static decimal[] SelectCloses(IReadOnlyList<Bar> bars)
    {
        var c = new decimal[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            c[i] = bars[i].C;
        }

        return c;
    }

    private static decimal?[] ToNullable(decimal[] values)
    {
        var outp = new decimal?[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            outp[i] = values[i];
        }

        return outp;
    }

    private static void ValidatePeriod(int period)
    {
        if (period <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "Period must be positive.");
        }
    }

    /// <summary>Newton-Raphson decimal square root (double seed, four refinement passes).</summary>
    internal static decimal Sqrt(decimal x)
    {
        if (x < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "Cannot take sqrt of a negative value.");
        }

        if (x == 0m)
        {
            return 0m;
        }

        var g = (decimal)Math.Sqrt((double)x);
        for (var i = 0; i < 4; i++)
        {
            g = (g + (x / g)) / 2m;
        }

        return g;
    }
}
