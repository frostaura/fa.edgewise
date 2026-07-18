using Edgewise.Domain.Engines.Performance;
using Shouldly;

namespace Edgewise.Domain.Tests.Performance;

public class PerformanceHelpersTests
{
    private static DateOnly D(int day) => new(2026, 1, day);

    [Fact]
    public void AnnualizedReturn_TwoYearsAtTwentyOnePercent_IsTenPercentPerYear()
    {
        // (1.21)^(365/730) - 1 = sqrt(1.21) - 1 = 10%.
        var r = PerformanceEngine.AnnualizedReturn(0.21m, 730);
        r.ShouldNotBeNull();
        r.Value.ShouldBe(0.10m, 0.000001m);
    }

    [Fact]
    public void AnnualizedReturn_ExactlyOneYear_IsUnchanged()
    {
        var r = PerformanceEngine.AnnualizedReturn(0.10m, 365);
        r.ShouldNotBeNull();
        r.Value.ShouldBe(0.10m, 0.000001m);
    }

    [Fact]
    public void AnnualizedReturn_ShortPeriodCompoundsUp()
    {
        // (1.01)^(365/30) - 1 = 0.1286937...
        var r = PerformanceEngine.AnnualizedReturn(0.01m, 30);
        r.ShouldNotBeNull();
        r.Value.ShouldBe(0.1286937m, 0.0001m);
    }

    [Fact]
    public void AnnualizedReturn_InvalidInputs_ReturnNull()
    {
        PerformanceEngine.AnnualizedReturn(0.10m, 0).ShouldBeNull();
        PerformanceEngine.AnnualizedReturn(0.10m, -5).ShouldBeNull();
        PerformanceEngine.AnnualizedReturn(-1m, 365).ShouldBeNull();   // total wipeout
        PerformanceEngine.AnnualizedReturn(-1.5m, 365).ShouldBeNull(); // beyond wipeout
    }

    [Fact]
    public void MaxDrawdown_PicksTheWorstPeakToTrough()
    {
        // Peaks/troughs: 12,000 -> 9,000 is 25%; 13,000 -> 8,000 is 5,000/13,000 = 38.46%.
        var dd = PerformanceEngine.MaxDrawdown(
        [
            new EquitySnapshot(D(1), 10_000),
            new EquitySnapshot(D(2), 12_000),
            new EquitySnapshot(D(3), 9_000),
            new EquitySnapshot(D(4), 9_500),
            new EquitySnapshot(D(5), 13_000),
            new EquitySnapshot(D(6), 8_000),
        ]);

        dd.ShouldBe(5_000m / 13_000m, 0.0000001m);
    }

    [Fact]
    public void MaxDrawdown_MonotonicRiseOrEmptySeries_IsZero()
    {
        PerformanceEngine.MaxDrawdown([]).ShouldBe(0m);
        PerformanceEngine.MaxDrawdown(
        [
            new EquitySnapshot(D(1), 10_000),
            new EquitySnapshot(D(2), 11_000),
            new EquitySnapshot(D(3), 12_000),
        ]).ShouldBe(0m);
    }

    [Fact]
    public void MaxDrawdown_UnsortedInput_IsSortedByDate()
    {
        var dd = PerformanceEngine.MaxDrawdown(
        [
            new EquitySnapshot(D(3), 6_000),
            new EquitySnapshot(D(1), 10_000),
            new EquitySnapshot(D(2), 12_000),
        ]);

        dd.ShouldBe(0.5m, 0.0000001m); // 12,000 -> 6,000
    }

    [Fact]
    public void MaxDrawdown_ZeroEquityStart_DoesNotDivideByZero()
    {
        var dd = PerformanceEngine.MaxDrawdown(
        [
            new EquitySnapshot(D(1), 0),
            new EquitySnapshot(D(2), 10_000),
            new EquitySnapshot(D(3), 7_500),
        ]);

        dd.ShouldBe(0.25m, 0.0000001m);
    }
}
