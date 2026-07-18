using Edgewise.Domain.Engines.Backtesting;
using Edgewise.Domain.Engines.Indicators;
using Shouldly;

namespace Edgewise.Domain.Tests.Backtesting;

public class BacktesterTests
{
    private static readonly DateTime T0 = new(2024, 1, 1);

    private static Bar B(int i, decimal o, decimal h, decimal l, decimal c, decimal v = 100m)
        => new(T0.AddDays(i), o, h, l, c, v);

    private static RuleTree MaCrossLong => new(
        [new Condition(IndicatorKind.PriceVsMa, new Dictionary<string, decimal> { ["period"] = 3m },
            ConditionOperator.CrossAbove, 0m)]);

    private static readonly CostModel CommissionOnly = new(
        CommissionPctPerSide: 0.1m, SpreadPct: 0m, SlippagePct: 0m);

    private static readonly RiskModel OneMillionRisk1Pct = new(RiskPctPerTrade: 1m, EquityStartMinor: 1_000_000);

    /// <summary>Closes 100,99,98,97,105: (C-SMA3)/SMA3 crosses above 0 exactly at index 4,
    /// so entry is at bars[5].Open.</summary>
    private static List<Bar> CrossAtIndex4Prefix() =>
    [
        B(0, 100, 101, 99, 100),
        B(1, 100, 100, 98, 99),
        B(2, 99, 99, 97, 98),
        B(3, 98, 98, 96, 97),
        B(4, 97, 106, 96, 105),
    ];

    // ---------------------------------------------------------------- no-look-ahead

    [Fact]
    public void Signal_true_only_on_final_bar_produces_no_trade()
    {
        // Flat closes make (C-SMA3)/SMA3 exactly 0 (not > 0); the last bar spikes so the
        // condition is true ONLY at the final index. There is no t+1 bar to enter on,
        // so a look-ahead-free backtester must produce zero trades.
        var bars = Enumerable.Range(0, 20).Select(i => B(i, 100, 100, 100, 100)).ToList();
        bars.Add(B(20, 100, 200, 100, 200));

        var tree = new RuleTree(
            [new Condition(IndicatorKind.PriceVsMa, new Dictionary<string, decimal> { ["period"] = 3m },
                ConditionOperator.Gt, 0m)]);
        var result = Backtester.Run(bars, tree, new ExitLadder(StopPct: 10m), CommissionOnly, OneMillionRisk1Pct);

        result.N.ShouldBe(0);
        result.EquityCurveMinor.ShouldAllBe(e => e == 1_000_000m);
    }

    // ---------------------------------------------------------------- entry & exit arithmetic

    [Fact]
    public void Ma_cross_enters_at_next_open_with_hand_verified_arithmetic()
    {
        // Signal at index 4 (see CrossAtIndex4Prefix), entry at bars[5].Open = 100.
        // Costs: commission 0.1%/side only (spread = slippage = 0), StopPct = 10, risk 1% of 1,000,000.
        // Hand math:
        //   entryFill    = 100 (no slippage/spread)
        //   stopDistance = 100 * 10% = 10 -> stop at 90
        //   qty          = 1,000,000 * 1% / 10 = 1,000 units; initial risk = 10,000 minor
        //   entry: cash  = 1,000,000 - 1,000*100 - 0.1%*100,000 = 899,900
        //   no stop touched (lows stay above 90); forced exit at final close 110:
        //   exit:  cash  = 899,900 + 1,000*110 - 0.1%*110,000 = 1,009,790
        //   pnl = 9,790 minor; R = 9,790 / 10,000 = 0.979
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 105, 99, 104));
        bars.Add(B(6, 104, 108, 103, 107));
        bars.Add(B(7, 107, 111, 106, 110));

        var result = Backtester.Run(bars, MaCrossLong, new ExitLadder(StopPct: 10m), CommissionOnly, OneMillionRisk1Pct);

        result.N.ShouldBe(1);
        var trade = result.Trades.Single();
        trade.EntryTs.ShouldBe(T0.AddDays(5)); // next bar after the signal, not the signal bar
        trade.ExitTs.ShouldBe(T0.AddDays(7));
        trade.Direction.ShouldBe(TradeDirection.Long);
        trade.EntryPx.ShouldBe(100m);
        trade.ExitPx.ShouldBe(110m);
        trade.PnlMinor.ShouldBe(9_790m);
        trade.RMultiple.ShouldBe(0.979m);
        result.WinRate.ShouldBe(1m);
        result.ExpectancyR.ShouldBe(0.979m);
        result.EquityCurveMinor[^1].ShouldBe(1_009_790m);
        result.EquityCurveMinor[5].ShouldBe(899_900m + (1_000m * 104m)); // mark-to-close
        result.ExposurePct.ShouldBe(3m / 8m * 100m);
    }

    [Fact]
    public void Slippage_and_spread_worsen_the_entry_fill()
    {
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 105, 99, 104));
        bars.Add(B(6, 104, 108, 103, 110));

        var costs = new CostModel(CommissionPctPerSide: 0m, SpreadPct: 0.2m, SlippagePct: 0.05m);
        var result = Backtester.Run(bars, MaCrossLong, new ExitLadder(StopPct: 10m), costs, OneMillionRisk1Pct);

        // Buy fill = 100 * (1 + (0.05 + 0.2/2)/100) = 100.15; sell fill = 110 * (1 - 0.0015) = 109.835.
        var trade = result.Trades.Single();
        trade.EntryPx.ShouldBe(100.15m);
        trade.ExitPx.ShouldBe(109.835m);
    }

    // ---------------------------------------------------------------- stop-first conservatism

    [Fact]
    public void Bar_hitting_both_stop_and_partial_target_fills_stop_first()
    {
        // Entry at 100, stop 90 (StopPct 10), partial target at +1R = 110.
        // Bar 6 spans 85..115, reaching BOTH -> conservative rule: full exit at the stop.
        // Hand math: qty 1,000; exit 90; pnl = 1,000*(90-100) - 100 (entry comm) - 90 (exit comm)
        //          = -10,190 minor; R = -1.019.
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 101, 99, 100));
        bars.Add(B(6, 100, 115, 85, 100));
        bars.Add(B(7, 100, 101, 99, 100));

        var ladder = new ExitLadder(PartialTakeAtR: 1m, PartialPct: 50m, StopPct: 10m);
        var result = Backtester.Run(bars, MaCrossLong, ladder, CommissionOnly, OneMillionRisk1Pct);

        var trade = result.Trades.Single();
        trade.ExitPx.ShouldBe(90m);
        trade.ExitTs.ShouldBe(T0.AddDays(6));
        trade.PnlMinor.ShouldBe(-10_190m);
        trade.RMultiple.ShouldBe(-1.019m);
    }

    [Fact]
    public void Gap_through_stop_fills_at_the_worse_open()
    {
        // Bar 6 opens at 80, far below the 90 stop -> fill at the open (80), not the stop.
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 101, 99, 100));
        bars.Add(B(6, 80, 82, 78, 81));

        var result = Backtester.Run(bars, MaCrossLong, new ExitLadder(StopPct: 10m), CommissionOnly, OneMillionRisk1Pct);
        result.Trades.Single().ExitPx.ShouldBe(80m);
    }

    // ---------------------------------------------------------------- ladder behavior

    [Fact]
    public void Breakeven_applies_from_the_next_bar_after_a_1R_touch()
    {
        // Entry 100, stop 90, BreakevenAtR 1. Bar 6 touches 110 (=+1R) intra-bar -> the stop
        // moves to entry (100) from bar 7 onward. Bar 7 dips to 95 -> exit at 100, not 90.
        // pnl = 0 gross - 100 - 100 commissions = -200 minor.
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 100.5m, 99.5m, 100));
        bars.Add(B(6, 100, 110.2m, 99, 105));
        bars.Add(B(7, 105, 106, 95, 96));

        var ladder = new ExitLadder(BreakevenAtR: 1m, StopPct: 10m);
        var result = Backtester.Run(bars, MaCrossLong, ladder, CommissionOnly, OneMillionRisk1Pct);

        var trade = result.Trades.Single();
        trade.ExitPx.ShouldBe(100m);
        trade.PnlMinor.ShouldBe(-200m);
    }

    [Fact]
    public void Pct_from_peak_trail_updates_on_close_and_exits_next_bar()
    {
        // Entry 100 (stop 90). Bar 5 closes at 120 -> 5% trail from peak close = 114 > 90.
        // Bar 6 trades down through 114 -> stop-out at 114 (min(open 118, stop 114)).
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 121, 99, 120));
        bars.Add(B(6, 118, 119, 110, 111));

        var ladder = new ExitLadder(TrailMethod: TrailMethod.PctFromPeak, TrailParam: 5m, StopPct: 10m);
        var result = Backtester.Run(bars, MaCrossLong, ladder, CommissionOnly, OneMillionRisk1Pct);

        result.Trades.Single().ExitPx.ShouldBe(114m);
    }

    [Fact]
    public void Time_stop_exits_at_the_close_of_the_nth_held_bar()
    {
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 103, 99, 102));
        bars.Add(B(6, 102, 105, 101, 104));
        bars.Add(B(7, 104, 105, 102, 103));
        bars.Add(B(8, 103, 105, 102, 104));

        var ladder = new ExitLadder(TimeStopBars: 2, StopPct: 10m);
        var result = Backtester.Run(bars, MaCrossLong, ladder, CommissionOnly, OneMillionRisk1Pct);

        var trade = result.Trades.Single();
        trade.ExitTs.ShouldBe(T0.AddDays(6)); // entry bar 5 counts as held bar 1
        trade.ExitPx.ShouldBe(104m);
    }

    [Fact]
    public void Partial_take_reduces_position_and_remainder_exits_later()
    {
        // Entry 100, stop 90, partial 50% at +1R (110): bar 6 tags 110 without touching 90.
        // Hand math: qty 1,000; partial sells 500 @ 110 (comm 55); remainder 500 forced out
        // at final close 112 (comm 56).
        //   cash: 1,000,000 - 100,000 - 100 = 899,900
        //       + 500*110 - 55 = 954,845
        //       + 500*112 - 56 = 1,010,789 -> pnl = 10,789; R = 1.0789
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 101, 99, 100));
        bars.Add(B(6, 100, 111, 99, 108));
        bars.Add(B(7, 108, 113, 107, 112));

        var ladder = new ExitLadder(PartialTakeAtR: 1m, PartialPct: 50m, StopPct: 10m);
        var result = Backtester.Run(bars, MaCrossLong, ladder, CommissionOnly, OneMillionRisk1Pct);

        var trade = result.Trades.Single();
        trade.PnlMinor.ShouldBe(10_789m);
        trade.RMultiple.ShouldBe(1.0789m);
    }

    [Fact]
    public void Atr_stop_uses_atr_at_signal_time()
    {
        // With StopAtrMult set the stop distance is fixed from ATR(14) at the signal bar;
        // warmup means no ATR -> no entry until ATR exists.
        var bars = new List<Bar>();
        for (var i = 0; i < 30; i++)
        {
            // Gentle alternation, then a cross late enough that ATR(14) is warm.
            var c = 100m + (i % 2 == 0 ? 0m : -1m);
            bars.Add(B(i, c, c + 1, c - 1, c));
        }

        bars.Add(B(30, 100, 106, 99, 105)); // close above SMA3 -> cross
        bars.Add(B(31, 105, 106, 104, 105));
        bars.Add(B(32, 105, 106, 104, 105));

        var ladder = new ExitLadder(StopAtrMult: 2m);
        var result = Backtester.Run(bars, MaCrossLong, ladder, CommissionOnly, OneMillionRisk1Pct);
        result.N.ShouldBe(1);
    }

    // ---------------------------------------------------------------- short support

    [Fact]
    public void Short_direction_profits_from_a_falling_market()
    {
        // Mirror setup: closes 100,101,102,103,95 -> CrossBelow 0 at index 4; short entry
        // at bars[5].Open = 95, stop at 95*1.1 = 104.5 (never touched), forced cover at 90.
        var bars = new List<Bar>
        {
            B(0, 100, 101, 99, 100),
            B(1, 100, 102, 99, 101),
            B(2, 101, 103, 100, 102),
            B(3, 102, 104, 101, 103),
            B(4, 103, 104, 94, 95),
            B(5, 95, 96, 90, 92),
            B(6, 92, 93, 88, 90),
        };
        var tree = new RuleTree(
            [new Condition(IndicatorKind.PriceVsMa, new Dictionary<string, decimal> { ["period"] = 3m },
                ConditionOperator.CrossBelow, 0m)],
            Direction: TradeDirection.Short);

        var result = Backtester.Run(bars, tree, new ExitLadder(StopPct: 10m), CommissionOnly, OneMillionRisk1Pct);

        var trade = result.Trades.Single();
        trade.Direction.ShouldBe(TradeDirection.Short);
        trade.EntryPx.ShouldBe(95m);
        trade.ExitPx.ShouldBe(90m);
        trade.PnlMinor.ShouldBeGreaterThan(0m);
    }

    // ---------------------------------------------------------------- costs & validation

    [Fact]
    public void Doubling_costs_strictly_reduces_expectancy()
    {
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 105, 99, 104));
        bars.Add(B(6, 104, 108, 103, 107));
        bars.Add(B(7, 107, 111, 106, 110));

        var normal = Backtester.Run(bars, MaCrossLong, new ExitLadder(StopPct: 10m), CommissionOnly, OneMillionRisk1Pct);
        var doubled = Backtester.Run(
            bars, MaCrossLong, new ExitLadder(StopPct: 10m),
            CommissionOnly with { CommissionPctPerSide = 0.2m }, OneMillionRisk1Pct);

        doubled.ExpectancyR.ShouldBeLessThan(normal.ExpectancyR);
    }

    [Fact]
    public void All_zero_cost_model_throws_by_construction()
    {
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 105, 99, 104));
        var zero = new CostModel(0m, 0m, 0m, 0m);

        Should.Throw<ArgumentException>(() =>
            Backtester.Run(bars, MaCrossLong, new ExitLadder(StopPct: 10m), zero, OneMillionRisk1Pct));
    }

    [Fact]
    public void Within_operator_requires_second_operand()
    {
        var bars = CrossAtIndex4Prefix();
        var tree = new RuleTree(
            [new Condition(IndicatorKind.RsiBand, new Dictionary<string, decimal>(), ConditionOperator.Within, 40m)]);
        Should.Throw<ArgumentException>(() =>
            Backtester.Run(bars, tree, new ExitLadder(StopPct: 10m), CommissionOnly, OneMillionRisk1Pct));
    }

    [Fact]
    public void Rule_tree_without_enabled_conditions_throws()
    {
        var bars = CrossAtIndex4Prefix();
        var tree = new RuleTree(
            [new Condition(IndicatorKind.RsiBand, new Dictionary<string, decimal>(), ConditionOperator.Gt, 50m, Enabled: false)]);
        Should.Throw<ArgumentException>(() =>
            Backtester.Run(bars, tree, new ExitLadder(StopPct: 10m), CommissionOnly, OneMillionRisk1Pct));
    }

    [Fact]
    public void Funding_cost_accrues_per_bar_held()
    {
        var bars = CrossAtIndex4Prefix();
        bars.Add(B(5, 100, 105, 99, 104));
        bars.Add(B(6, 104, 108, 103, 107));
        bars.Add(B(7, 107, 111, 106, 110));

        var noFunding = Backtester.Run(bars, MaCrossLong, new ExitLadder(StopPct: 10m), CommissionOnly, OneMillionRisk1Pct);
        var withFunding = Backtester.Run(
            bars, MaCrossLong, new ExitLadder(StopPct: 10m),
            CommissionOnly with { FundingPctPer8h = 0.01m }, OneMillionRisk1Pct);

        withFunding.Trades.Single().PnlMinor.ShouldBeLessThan(noFunding.Trades.Single().PnlMinor);
    }
}
