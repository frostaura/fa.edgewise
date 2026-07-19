namespace Edgewise.Domain.Engines.Analytics;

/// <summary>
/// Flattened, pre-resolved statistics for a single closed trade.
/// <see cref="HourOfDay"/> and <see cref="DayOfWeek"/> are already in the user's timezone
/// (the caller applies the conversion).
/// </summary>
public sealed record TradeStat
{
    public DateTime ClosedAt { get; init; }
    public decimal? RRealised { get; init; }
    public decimal? RPlanned { get; init; }
    public long PnlMinor { get; init; }
    public string SetupTag { get; init; } = "";
    public string InstrumentKey { get; init; } = "";
    public string EmotionTag { get; init; } = "";
    public bool HadPlan { get; init; }
    public int? AdherenceScore { get; init; }
    public long? HoldingSeconds { get; init; }
    public bool IsWin { get; init; }
    public DayOfWeek DayOfWeek { get; init; }
    public int HourOfDay { get; init; }
}

/// <summary>Expectancy statistics for one group of trades.</summary>
public sealed record ExpectancyGroup<TKey>(
    TKey Key,
    int N,
    decimal WinRate,
    decimal AvgWinR,
    decimal AvgLossR,
    decimal ExpectancyR,
    double Wilson95Lo,
    double Wilson95Hi,
    bool LowSample);

/// <summary>Grouped expectancy report.</summary>
public sealed record ExpectancyReport<TKey>(IReadOnlyList<ExpectancyGroup<TKey>> Groups);

/// <summary>One histogram bin covering [From, To) in R units.</summary>
public sealed record RBin(decimal From, decimal To, int Count);

/// <summary>Histogram of realised R multiples.</summary>
public sealed record RDistributionReport(decimal BinSize, IReadOnlyList<RBin> Bins, int N);

/// <summary>A point on the account equity curve, equity in minor currency units.</summary>
public readonly record struct EquityPoint(DateTime At, long EquityMinor);

/// <summary>Equity point annotated with running peak and drawdown (both minor units).</summary>
public sealed record DrawdownPoint(DateTime At, long EquityMinor, long PeakMinor, long DrawdownMinor);

/// <summary>Drawdown series plus max and current drawdown, in minor currency units.</summary>
public sealed record DrawdownReport(
    IReadOnlyList<DrawdownPoint> Series,
    long MaxDrawdownMinor,
    long CurrentDrawdownMinor);

/// <summary>Plan-then-trade ratio for one ISO week (week starts Monday). Rate is a fraction 0..1.</summary>
public sealed record WeeklyPlanRate(DateOnly WeekStart, int N, int PlannedN, decimal PlannedRate);

/// <summary>Average adherence score for one week (week starts Monday), over trades that have a score.</summary>
public sealed record WeeklyAdherence(DateOnly WeekStart, int N, decimal AvgAdherence);

/// <summary>One calendar-day cell of the heatmap.</summary>
public sealed record CalendarCell(DateOnly Date, decimal SumR, decimal? AvgAdherence, int N);

/// <summary>Aggregate per hour-of-day (user timezone).</summary>
public sealed record HourBucket(int HourOfDay, int N, decimal SumR);

/// <summary>
/// Tilt signature: performance of trades entered shortly after a loss vs the baseline.
/// Significance uses a Welch t-test approximation (p &lt; 0.05, two-sided) and requires
/// at least 10 after-loss trades.
/// </summary>
public sealed record TiltSignatureReport(
    int NAfterLoss,
    decimal ExpectancyAfterLoss,
    decimal BaselineExpectancy,
    decimal DeltaR,
    bool IsSignificant);
