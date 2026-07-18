namespace Edgewise.Domain.Engines.Backtesting;

/// <summary>Which indicator a condition reads. Each kind maps to a scalar, strictly causal
/// series s[t] that operators compare against the condition's operand(s):
/// <list type="bullet">
/// <item><b>PriceVsMa</b> (params: period, default 20): s = (close - SMA(period)) / SMA(period).
/// Gt 0 = price above MA; CrossAbove 0 = MA crossover.</item>
/// <item><b>RsiBand</b> (params: period, default 14): s = RSI. e.g. Lt 30, Within 40..60.</item>
/// <item><b>BreakoutNBarHigh</b> (params: n, default 20): s = (close - max High of previous n
/// bars) / that max. Gt 0 = close broke the n-bar high.</item>
/// <item><b>VolumeVsAvg</b> (params: period, default 20): s = volume / SMA(volume, period).
/// Gt 1.5 = 150% of average volume.</item>
/// <item><b>MacdCross</b> (params: fast/slow/signal, defaults 12/26/9): s = macd - signal
/// (the histogram). CrossAbove 0 = bullish cross.</item>
/// <item><b>PriceVsBollinger</b> (params: period default 20, sd default 2):
/// s = (close - mid) / (upper - mid); +1 at the upper band, -1 at the lower, 0 when the
/// band has zero width. Gt 1 = close above upper band.</item>
/// </list></summary>
public enum IndicatorKind
{
    PriceVsMa,
    RsiBand,
    BreakoutNBarHigh,
    VolumeVsAvg,
    MacdCross,
    PriceVsBollinger,
}

public enum ConditionOperator
{
    /// <summary>s[t] &gt; Operand.</summary>
    Gt,

    /// <summary>s[t] &lt; Operand.</summary>
    Lt,

    /// <summary>s[t-1] &lt;= Operand and s[t] &gt; Operand.</summary>
    CrossAbove,

    /// <summary>s[t-1] &gt;= Operand and s[t] &lt; Operand.</summary>
    CrossBelow,

    /// <summary>Operand &lt;= s[t] &lt;= Operand2.</summary>
    Within,
}

public enum RuleCombinator
{
    And,
}

public enum TradeDirection
{
    Long,
    Short,
}

public enum TrailMethod
{
    None,

    /// <summary>Trail at close -/+ TrailParam * ATR(14), updated on bar close (effective next bar).</summary>
    AtrMult,

    /// <summary>Trail TrailParam percent from the best close since entry, updated on bar close.</summary>
    PctFromPeak,
}

/// <summary>One entry condition. A null series value (indicator warmup) evaluates to false.</summary>
public sealed record Condition(
    IndicatorKind Indicator,
    IReadOnlyDictionary<string, decimal> Params,
    ConditionOperator Operator,
    decimal Operand,
    decimal? Operand2 = null,
    bool Enabled = true);

/// <summary>Entry rule: all enabled conditions must hold on the signal bar (AND).</summary>
public sealed record RuleTree(
    IReadOnlyList<Condition> Conditions,
    RuleCombinator Combinator = RuleCombinator.And,
    TradeDirection Direction = TradeDirection.Long);

/// <summary>
/// Exit ladder. Initial stop distance resolution: StopAtrMult * ATR(14) at the signal bar
/// when StopAtrMult is set; otherwise entry price * StopPct/100 when StopPct is set;
/// otherwise the honest default of 2 * ATR(14).
/// BreakevenAtR: once price touches entry +/- BreakevenAtR * R intra-bar, the stop moves to
/// entry from the NEXT bar onward. PartialTakeAtR/PartialPct: take PartialPct% of the
/// original size at entry +/- PartialTakeAtR * R (skipped if the stop is hit in the same
/// bar - stop first). TimeStopBars: exit at the close of the Nth held bar (entry bar = 1).
/// </summary>
public sealed record ExitLadder(
    decimal? BreakevenAtR = null,
    decimal? PartialTakeAtR = null,
    decimal PartialPct = 0m,
    TrailMethod TrailMethod = TrailMethod.None,
    decimal TrailParam = 0m,
    int? TimeStopBars = null,
    decimal? StopAtrMult = null,
    decimal? StopPct = null);

/// <summary>
/// Trading frictions, all in percent. At least one component must be non-zero - a zero-cost
/// backtest is dishonest by construction and is rejected.
/// Fills: buys pay open/stop/target price * (1 + (SlippagePct + SpreadPct/2)/100), sells
/// receive price * (1 - ...). Commission is charged per side on filled notional.
/// FundingPctPer8h accrues per 8 hours held on the marked notional (longs and shorts alike),
/// using bar timestamp deltas (first bar of a series assumes 24h).
/// </summary>
public sealed record CostModel(
    decimal CommissionPctPerSide,
    decimal SpreadPct,
    decimal SlippagePct,
    decimal FundingPctPer8h = 0m);

/// <summary>Fixed-fractional risk sizing: quantity = equity * RiskPctPerTrade/100 / stopDistance.</summary>
public sealed record RiskModel(decimal RiskPctPerTrade, long EquityStartMinor);

/// <summary>
/// One completed round trip. EntryPx/ExitPx are the actual (cost-adjusted) fill prices of
/// the initial entry and the final exit. PnlMinor includes partials, commissions and
/// funding. RMultiple = PnlMinor / (initial quantity * initial stop distance).
/// </summary>
public sealed record Trade(
    DateTime EntryTs,
    DateTime ExitTs,
    TradeDirection Direction,
    decimal EntryPx,
    decimal ExitPx,
    decimal RMultiple,
    decimal PnlMinor);

/// <summary>
/// Backtest output. WinRate is a fraction in [0,1] (trades with PnL &gt; 0).
/// ExpectancyR and AvgR are both the mean trade R-multiple (expectancy expressed in R).
/// MaxDrawdownPct is the largest peak-to-trough decline of the mark-to-close equity curve,
/// in percent. ExposurePct = bars with an open position / total bars * 100.
/// </summary>
public sealed record BacktestResult(
    int N,
    decimal WinRate,
    decimal ExpectancyR,
    decimal AvgR,
    decimal MaxDrawdownPct,
    decimal ExposurePct,
    IReadOnlyList<Trade> Trades,
    IReadOnlyList<decimal> EquityCurveMinor);

public enum SampleVerdict
{
    /// <summary>n &gt;= 300 trades.</summary>
    Adequate,

    /// <summary>100-299 trades.</summary>
    Thin,

    /// <summary>&lt; 100 trades.</summary>
    Exploratory,
}

/// <summary>One parameter-wiggle rerun: the condition's operand moved by +/-20%.</summary>
public sealed record WiggleEntry(
    int ConditionIndex,
    string OperandName,
    decimal OriginalValue,
    decimal WiggledValue,
    decimal ExpectancyRDelta);

/// <summary>
/// The honest backtest report. InSample/OutOfSample are the chronological 70/30 split, each
/// run independently; both are null when either segment would have fewer than
/// <see cref="Backtester.MinSplitBars"/> bars. DoubledCosts re-runs the full sample with all
/// cost components doubled. MaxWiggleSensitivityR is the largest absolute expectancy delta
/// across all wiggle reruns. SampleSizeRedFlag is true when the verdict is Exploratory.
/// IsExploratory is true when the OOS segment is absent OR the verdict is Exploratory.
/// </summary>
public sealed record HonestyReport(
    BacktestResult FullSample,
    BacktestResult? InSample,
    BacktestResult? OutOfSample,
    BacktestResult DoubledCosts,
    IReadOnlyList<WiggleEntry> Wiggles,
    decimal MaxWiggleSensitivityR,
    SampleVerdict SampleVerdict,
    bool SampleSizeRedFlag,
    bool IsExploratory);
