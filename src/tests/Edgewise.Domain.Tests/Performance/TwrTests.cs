using Edgewise.Domain.Engines.Performance;
using Shouldly;

namespace Edgewise.Domain.Tests.Performance;

public class TwrTests
{
    private static DateOnly D(int day) => new(2026, 1, day);

    [Fact]
    public void ThreePeriodFixture_WithMidPeriodDeposit_HandComputed()
    {
        // All amounts in minor units (cents).
        //
        // Jan 1: equity 100,000.00 (baseline).
        // Period 1 (Jan1 -> Jan2): no flow.
        //   r1 = 110,000 / 100,000 - 1 = 10%.
        // Period 2 (Jan2 -> Jan3): deposit 22,000.00 start-of-day Jan 3.
        //   r2 = (143,000 - 22,000) / 110,000 - 1 = 121,000 / 110,000 - 1 = 10%.
        // Period 3 (Jan3 -> Jan4): no flow.
        //   r3 = 157,300 / 143,000 - 1 = 10%.
        // TWR = 1.1 * 1.1 * 1.1 - 1 = 33.1%.
        var snapshots = new List<EquitySnapshot>
        {
            new(D(1), 100_000_00),
            new(D(2), 110_000_00),
            new(D(3), 143_000_00),
            new(D(4), 157_300_00),
        };
        var flows = new List<NetFlow> { new(D(3), 22_000_00) };

        var result = PerformanceEngine.ComputeTwr(snapshots, flows);

        result.Points.Count.ShouldBe(3);
        result.Points[0].ShouldBe(new TwrPoint(D(2), 0.1m, 0.1m));
        result.Points[1].PeriodReturn.ShouldBe(0.1m, 0.0000001m);
        result.Points[1].CumulativeReturn.ShouldBe(0.21m, 0.0000001m);
        result.Points[2].PeriodReturn.ShouldBe(0.1m, 0.0000001m);
        result.Points[2].CumulativeReturn.ShouldBe(0.331m, 0.0000001m);
        result.TotalReturn.ShouldBe(0.331m, 0.0000001m);
    }

    [Fact]
    public void DepositTiming_DoesNotChangeTwr_ButChangesXirr()
    {
        // Same three quarterly sub-period returns (+10%, +20%, -5%) in both scenarios;
        // only the timing of a large 100,000.00 deposit differs.
        //
        // Scenario A: deposit lands start-of-day Apr 1 (before the strong +20% quarter).
        //   E(Jan1)=1,000,000; r1=10% => E(Apr1) = 1.1*1,000,000 + 10,000,000 = 11,100,000
        //   r2=20% => E(Jul1) = 1.2*11,100,000 = 13,320,000
        //   r3=-5% => E(Oct1) = 0.95*13,320,000 = 12,654,000
        // Scenario B: deposit lands start-of-day Jul 1 (before the losing -5% quarter).
        //   E(Jan1)=1,000,000; E(Apr1)=1,100,000; E(Jul1) = 1.2*1,100,000 + 10,000,000 = 11,320,000
        //   E(Oct1) = 0.95*11,320,000 = 10,754,000
        //
        // TWR (both): 1.1 * 1.2 * 0.95 - 1 = 25.4% -- flow timing is stripped out.
        // XIRR (money-weighted): A's deposit rides +20% then -5% (positive rate);
        // B's deposit only rides -5% (negative rate). MWR must disambiguate them.
        var jan1 = new DateOnly(2025, 1, 1);
        var apr1 = new DateOnly(2025, 4, 1);
        var jul1 = new DateOnly(2025, 7, 1);
        var oct1 = new DateOnly(2025, 10, 1);

        var snapshotsA = new List<EquitySnapshot>
        {
            new(jan1, 1_000_000),
            new(apr1, 11_100_000),
            new(jul1, 13_320_000),
            new(oct1, 12_654_000),
        };
        var flowsA = new List<NetFlow> { new(apr1, 10_000_000) };

        var snapshotsB = new List<EquitySnapshot>
        {
            new(jan1, 1_000_000),
            new(apr1, 1_100_000),
            new(jul1, 11_320_000),
            new(oct1, 10_754_000),
        };
        var flowsB = new List<NetFlow> { new(jul1, 10_000_000) };

        var twrA = PerformanceEngine.ComputeTwr(snapshotsA, flowsA);
        var twrB = PerformanceEngine.ComputeTwr(snapshotsB, flowsB);

        twrA.TotalReturn.ShouldBe(0.254m, 0.0000001m);
        twrB.TotalReturn.ShouldBe(0.254m, 0.0000001m);
        twrA.TotalReturn.ShouldBe(twrB.TotalReturn, 0.0000001m);

        // Money-weighted (XIRR) sees the difference in flow timing.
        var xirrA = PerformanceEngine.ComputeXirr(
        [
            new CashflowItem(jan1, -1_000_000),
            new CashflowItem(apr1, -10_000_000),
            new CashflowItem(oct1, 12_654_000),
        ]);
        var xirrB = PerformanceEngine.ComputeXirr(
        [
            new CashflowItem(jan1, -1_000_000),
            new CashflowItem(jul1, -10_000_000),
            new CashflowItem(oct1, 10_754_000),
        ]);

        xirrA.ShouldNotBeNull();
        xirrB.ShouldNotBeNull();
        xirrA.Value.ShouldBeGreaterThan(0m);
        xirrB.Value.ShouldBeLessThan(0m);
    }

    [Fact]
    public void ZeroEquityPeriod_IsSkippedSafely()
    {
        // Starting equity of zero makes the sub-period return undefined; it must be
        // skipped (period return 0, no geometric contribution) without dividing by zero.
        var snapshots = new List<EquitySnapshot>
        {
            new(D(1), 0),
            new(D(2), 50_000),
            new(D(3), 55_000),
        };

        var result = PerformanceEngine.ComputeTwr(snapshots, []);

        result.Points.Count.ShouldBe(2);
        result.Points[0].PeriodReturn.ShouldBe(0m);
        result.Points[1].PeriodReturn.ShouldBe(0.1m, 0.0000001m);
        result.TotalReturn.ShouldBe(0.1m, 0.0000001m);
    }

    [Fact]
    public void FlowOnFirstSnapshotDate_IsIgnored()
    {
        var snapshots = new List<EquitySnapshot>
        {
            new(D(1), 100_000),
            new(D(2), 110_000),
        };

        var withFlow = PerformanceEngine.ComputeTwr(
            snapshots, [new NetFlow(D(1), 999_999)]);
        var withoutFlow = PerformanceEngine.ComputeTwr(snapshots, []);

        withFlow.TotalReturn.ShouldBe(withoutFlow.TotalReturn);
    }

    [Fact]
    public void WithdrawalsAreNegativeFlows()
    {
        // Withdraw 10,000 start-of-day Jan 2; equity ends at 99,000.
        // r = (99,000 - (-10,000)) / 100,000 - 1 = 109,000/100,000 - 1 = 9%.
        var result = PerformanceEngine.ComputeTwr(
            [new EquitySnapshot(D(1), 100_000), new EquitySnapshot(D(2), 99_000)],
            [new NetFlow(D(2), -10_000)]);

        result.TotalReturn.ShouldBe(0.09m, 0.0000001m);
    }

    [Fact]
    public void UnsortedSnapshotsAndMultipleFlowsPerPeriod_AreHandledDeterministically()
    {
        // Snapshots supplied out of order; two flows within one period are summed.
        // Period Jan1 -> Jan3 (no Jan2 snapshot): F = 5,000 + 3,000 = 8,000.
        // r = (120,000 - 8,000)/100,000 - 1 = 12%.
        var result = PerformanceEngine.ComputeTwr(
            [new EquitySnapshot(D(3), 120_000), new EquitySnapshot(D(1), 100_000)],
            [new NetFlow(D(2), 5_000), new NetFlow(D(3), 3_000)]);

        var point = result.Points.ShouldHaveSingleItem();
        point.Date.ShouldBe(D(3));
        result.TotalReturn.ShouldBe(0.12m, 0.0000001m);
    }

    [Fact]
    public void EmptyOrSingleSnapshot_YieldsEmptySeriesAndZeroTotal()
    {
        PerformanceEngine.ComputeTwr([], []).ShouldSatisfyAllConditions(
            r => r.Points.ShouldBeEmpty(),
            r => r.TotalReturn.ShouldBe(0m));

        PerformanceEngine.ComputeTwr([new EquitySnapshot(D(1), 100_000)], [])
            .ShouldSatisfyAllConditions(
                r => r.Points.ShouldBeEmpty(),
                r => r.TotalReturn.ShouldBe(0m));
    }
}
