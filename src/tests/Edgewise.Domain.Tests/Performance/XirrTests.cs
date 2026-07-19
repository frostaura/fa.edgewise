using Edgewise.Domain.Engines.Performance;
using Shouldly;

namespace Edgewise.Domain.Tests.Performance;

public class XirrTests
{
    [Fact]
    public void ClassicOneYearRoundTrip_IsExactlyTenPercent()
    {
        // Invest 10,000.00 on 2019-01-01, receive 11,000.00 on 2020-01-01 (exactly 365
        // days, non-leap span): 11,000/(1+r)^1 = 10,000 => r = 10%. This matches Excel
        // XIRR(-10000, 11000; 01/01/2019, 01/01/2020) = 0.10 (Actual/365 convention).
        var rate = PerformanceEngine.ComputeXirr(
        [
            new CashflowItem(new DateOnly(2019, 1, 1), -1_000_000),
            new CashflowItem(new DateOnly(2020, 1, 1), 1_100_000),
        ]);

        rate.ShouldNotBeNull();
        rate.Value.ShouldBe(0.10m, 0.0001m);
    }

    [Fact]
    public void NegativeRateRoundTrip_IsExactlyMinusTenPercent()
    {
        // 9,000 back on 10,000 invested over exactly 365 days: r = -10%.
        var rate = PerformanceEngine.ComputeXirr(
        [
            new CashflowItem(new DateOnly(2019, 1, 1), -1_000_000),
            new CashflowItem(new DateOnly(2020, 1, 1), 900_000),
        ]);

        rate.ShouldNotBeNull();
        rate.Value.ShouldBe(-0.10m, 0.0001m);
    }

    [Fact]
    public void TwoAnnualProceeds_MatchesQuadraticClosedForm()
    {
        // -1,000.00 at t=0, +500.00 at t=1y (365d), +600.00 at t=2y (730d).
        // With x = 1/(1+r): 600x^2 + 500x - 1000 = 0
        //   x = (-500 + sqrt(500^2 + 4*600*1000)) / (2*600) = (sqrt(2,650,000) - 500)/1200
        //   r = 1/x - 1 = 0.0639410298...
        var rate = PerformanceEngine.ComputeXirr(
        [
            new CashflowItem(new DateOnly(2020, 1, 1), -100_000),
            new CashflowItem(new DateOnly(2020, 12, 31), 50_000), // +365 days
            new CashflowItem(new DateOnly(2021, 12, 31), 60_000), // +730 days
        ]);

        var expected = (decimal)(1200.0 / (Math.Sqrt(2_650_000.0) - 500.0) - 1.0);

        rate.ShouldNotBeNull();
        rate.Value.ShouldBe(expected, 0.0001m);
        rate.Value.ShouldBe(0.0639410m, 0.0001m);
    }

    [Fact]
    public void IrregularMultiFlowCase_MatchesReferenceSolverWithinTolerance()
    {
        // Excel-style XIRR (Actual/365):
        //   2020-01-10  -10,000.00
        //   2020-04-15   -2,500.00
        //   2020-09-01   +3,000.00
        //   2021-01-10  +11,000.00
        // Reference root of the NPV polynomial (independent bisection solver): 0.13869897.
        var flows = new List<CashflowItem>
        {
            new(new DateOnly(2020, 1, 10), -1_000_000),
            new(new DateOnly(2020, 4, 15), -250_000),
            new(new DateOnly(2020, 9, 1), 300_000),
            new(new DateOnly(2021, 1, 10), 1_100_000),
        };

        var rate = PerformanceEngine.ComputeXirr(flows);

        rate.ShouldNotBeNull();
        rate.Value.ShouldBe(0.1386990m, 0.0001m);

        // The returned rate really is a root: NPV at that rate is ~0.
        var t0 = flows[0].Date;
        var npv = flows.Sum(f =>
            (double)f.AmountMinor
            * Math.Pow(1.0 + (double)rate.Value, -((f.Date.DayNumber - t0.DayNumber) / 365.0)));
        Math.Abs(npv).ShouldBeLessThan(1.0); // within one minor unit on ~1m minor notional
    }

    [Fact]
    public void UnsortedInput_IsSortedByDate()
    {
        var rate = PerformanceEngine.ComputeXirr(
        [
            new CashflowItem(new DateOnly(2020, 1, 1), 1_100_000),
            new CashflowItem(new DateOnly(2019, 1, 1), -1_000_000),
        ]);

        rate.ShouldNotBeNull();
        rate.Value.ShouldBe(0.10m, 0.0001m);
    }

    [Fact]
    public void NoSignChangeInAmounts_ReturnsNull()
    {
        PerformanceEngine.ComputeXirr(
        [
            new CashflowItem(new DateOnly(2020, 1, 1), -1_000),
            new CashflowItem(new DateOnly(2020, 6, 1), -2_000),
        ]).ShouldBeNull();

        PerformanceEngine.ComputeXirr(
        [
            new CashflowItem(new DateOnly(2020, 1, 1), 1_000),
            new CashflowItem(new DateOnly(2020, 6, 1), 2_000),
        ]).ShouldBeNull();
    }

    [Fact]
    public void FewerThanTwoFlows_ReturnsNull()
    {
        PerformanceEngine.ComputeXirr([]).ShouldBeNull();
        PerformanceEngine.ComputeXirr(
            [new CashflowItem(new DateOnly(2020, 1, 1), -1_000)]).ShouldBeNull();
    }

    [Fact]
    public void RootOutsideBracket_ReturnsNull()
    {
        // Turning 100 into 100,000 in two days implies an annualised rate far above the
        // 1000% upper bound: NPV has no sign change on [-0.9999, 10] and must yield null.
        PerformanceEngine.ComputeXirr(
        [
            new CashflowItem(new DateOnly(2020, 1, 1), -100),
            new CashflowItem(new DateOnly(2020, 1, 3), 100_000),
        ]).ShouldBeNull();
    }
}
