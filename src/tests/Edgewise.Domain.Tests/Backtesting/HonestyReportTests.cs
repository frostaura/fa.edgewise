using Edgewise.Domain.Engines.Backtesting;
using Edgewise.Domain.Engines.Indicators;
using Shouldly;

namespace Edgewise.Domain.Tests.Backtesting;

public class HonestyReportTests
{
    private static readonly DateTime T0 = new(2024, 1, 1);

    private static readonly CostModel SmallCommission = new(
        CommissionPctPerSide: 0.01m, SpreadPct: 0m, SlippagePct: 0m);

    private static readonly RiskModel Risk = new(RiskPctPerTrade: 1m, EquityStartMinor: 1_000_000);

    /// <summary>Every close breaks the previous bar's high, so BreakoutNBarHigh(n=1) Gt 0
    /// signals on every bar; a 1-bar time stop turns that into a trade per bar.</summary>
    private static RuleTree BreakoutEveryBar => new(
        [new Condition(IndicatorKind.BreakoutNBarHigh, new Dictionary<string, decimal> { ["n"] = 1m },
            ConditionOperator.Gt, 0m)]);

    private static ExitLadder OneBarHold => new(TimeStopBars: 1, StopPct: 5m);

    private static List<Bar> RisingBars(int count)
        => Enumerable.Range(0, count)
            .Select(i => new Bar(T0.AddDays(i), 99.5m + i, 100.2m + i, 99.3m + i, 100m + i, 100m))
            .ToList();

    [Fact]
    public void Large_sample_report_is_adequate_with_oos_and_no_red_flags()
    {
        var report = Backtester.Build(RisingBars(700), BreakoutEveryBar, OneBarHold, SmallCommission, Risk);

        report.FullSample.N.ShouldBeGreaterThanOrEqualTo(300);
        report.SampleVerdict.ShouldBe(SampleVerdict.Adequate);
        report.SampleSizeRedFlag.ShouldBeFalse();
        report.IsExploratory.ShouldBeFalse();

        // 70/30 chronological split, both segments run independently.
        // isCount = (int)(700 * 0.7) truncates to 489 (double representation of 0.7).
        report.InSample.ShouldNotBeNull();
        report.OutOfSample.ShouldNotBeNull();
        report.InSample!.EquityCurveMinor.Count.ShouldBe(489);
        report.OutOfSample!.EquityCurveMinor.Count.ShouldBe(211);
        report.InSample.N.ShouldBeGreaterThan(0);
        report.OutOfSample.N.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Doubled_costs_run_has_strictly_lower_expectancy()
    {
        var report = Backtester.Build(RisingBars(700), BreakoutEveryBar, OneBarHold, SmallCommission, Risk);
        report.DoubledCosts.ExpectancyR.ShouldBeLessThan(report.FullSample.ExpectancyR);
    }

    [Fact]
    public void Wiggle_runs_are_reproducible_and_cover_every_operand()
    {
        var bars = RisingBars(200);
        var r1 = Backtester.Build(bars, BreakoutEveryBar, OneBarHold, SmallCommission, Risk);
        var r2 = Backtester.Build(bars, BreakoutEveryBar, OneBarHold, SmallCommission, Risk);

        // Deterministic engine: identical inputs -> identical wiggle entries and expectancies.
        r1.FullSample.ExpectancyR.ShouldBe(r2.FullSample.ExpectancyR);
        r1.Wiggles.SequenceEqual(r2.Wiggles).ShouldBeTrue();
        r1.MaxWiggleSensitivityR.ShouldBe(r2.MaxWiggleSensitivityR);

        // One enabled condition with a single operand -> exactly the +/-20% pair.
        r1.Wiggles.Count.ShouldBe(2);
        r1.Wiggles.Select(w => w.WiggledValue).ShouldBe([0m * 0.8m, 0m * 1.2m]);
    }

    [Fact]
    public void Wiggle_moves_operands_by_twenty_percent_and_reports_sensitivity()
    {
        // A non-zero operand (RSI Within 0..80 alongside the breakout) produces real
        // +/-20% wiggles: 64/96 for the upper bound, 0/0 for the lower.
        var tree = new RuleTree(
        [
            BreakoutEveryBar.Conditions[0],
            new Condition(IndicatorKind.RsiBand, new Dictionary<string, decimal> { ["period"] = 5m },
                ConditionOperator.Within, 0m, 80m),
        ]);
        var report = Backtester.Build(RisingBars(200), tree, OneBarHold, SmallCommission, Risk);

        report.Wiggles.Count.ShouldBe(6); // (1 operand + 2 operands) x 2 factors
        var upper = report.Wiggles.Where(w => w.ConditionIndex == 1 && w.OperandName == "Operand2").ToList();
        upper.Select(w => w.WiggledValue).ShouldBe([64m, 96m]);
        report.MaxWiggleSensitivityR.ShouldBe(report.Wiggles.Max(w => Math.Abs(w.ExpectancyRDelta)));

        // RSI of a monotonically rising series is 100, outside Within 0..80: the extra
        // condition kills every entry, so the full-sample run has no trades...
        report.FullSample.N.ShouldBe(0);
    }

    [Fact]
    public void Disabled_conditions_are_not_wiggled()
    {
        var tree = new RuleTree(
        [
            BreakoutEveryBar.Conditions[0],
            new Condition(IndicatorKind.RsiBand, new Dictionary<string, decimal>(),
                ConditionOperator.Gt, 50m, Enabled: false),
        ]);
        var report = Backtester.Build(RisingBars(150), tree, OneBarHold, SmallCommission, Risk);
        report.Wiggles.ShouldAllBe(w => w.ConditionIndex == 0);
    }

    [Fact]
    public void Too_few_bars_for_oos_split_forces_exploratory()
    {
        // 80 bars: IS would be 55, OOS only 25 (< 50 minimum) -> no split, exploratory.
        var report = Backtester.Build(RisingBars(80), BreakoutEveryBar, OneBarHold, SmallCommission, Risk);

        report.InSample.ShouldBeNull();
        report.OutOfSample.ShouldBeNull();
        report.IsExploratory.ShouldBeTrue();
    }

    [Fact]
    public void Under_100_trades_is_exploratory_with_red_flag()
    {
        // 150 bars produce ~148 trades? No: one trade per bar -> ~147, which is Thin.
        // Use a sparse signal instead: a single MA cross yields 1 trade -> Exploratory.
        var bars = new List<Bar>
        {
            new(T0, 100, 101, 99, 100, 100),
            new(T0.AddDays(1), 100, 100, 98, 99, 100),
            new(T0.AddDays(2), 99, 99, 97, 98, 100),
            new(T0.AddDays(3), 98, 98, 96, 97, 100),
            new(T0.AddDays(4), 97, 106, 96, 105, 100),
        };
        for (var i = 5; i < 160; i++)
        {
            bars.Add(new Bar(T0.AddDays(i), 105, 106, 104, 105, 100));
        }

        var tree = new RuleTree(
            [new Condition(IndicatorKind.PriceVsMa, new Dictionary<string, decimal> { ["period"] = 3m },
                ConditionOperator.CrossAbove, 0m)]);
        var report = Backtester.Build(bars, tree, new ExitLadder(TimeStopBars: 3, StopPct: 10m), SmallCommission, Risk);

        report.FullSample.N.ShouldBeGreaterThan(0);
        report.FullSample.N.ShouldBeLessThan(100);
        report.SampleVerdict.ShouldBe(SampleVerdict.Exploratory);
        report.SampleSizeRedFlag.ShouldBeTrue();
        report.IsExploratory.ShouldBeTrue();
    }

    [Fact]
    public void Between_100_and_299_trades_is_thin()
    {
        var report = Backtester.Build(RisingBars(150), BreakoutEveryBar, OneBarHold, SmallCommission, Risk);
        report.FullSample.N.ShouldBeInRange(100, 299);
        report.SampleVerdict.ShouldBe(SampleVerdict.Thin);
        report.SampleSizeRedFlag.ShouldBeFalse();
        // 150 bars: IS 104, OOS 46 -> below the 50-bar minimum, so still exploratory.
        report.OutOfSample.ShouldBeNull();
        report.IsExploratory.ShouldBeTrue();
    }

    [Fact]
    public void Zero_cost_model_is_rejected_at_report_level_too()
    {
        Should.Throw<ArgumentException>(() => Backtester.Build(
            RisingBars(100), BreakoutEveryBar, OneBarHold, new CostModel(0m, 0m, 0m, 0m), Risk));
    }
}
