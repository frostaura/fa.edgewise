using Edgewise.Domain.Engines.Positions;
using Shouldly;

namespace Edgewise.Domain.Tests.Positions;

public class MaeMfeTests
{
    private static readonly (decimal High, decimal Low)[] Bars =
    [
        (105m, 98m),
        (110m, 99m),
        (103m, 101m),
    ];

    [Fact]
    public void Long_MaeIsWorstDipBelowEntry_MfeIsBestRiseAboveEntry()
    {
        // Entry 100, min low 98, max high 110: MAE = 2%, MFE = 10%.
        var (mae, mfe) = PositionBuilder.ComputeMaeMfe(100m, TradeDirection.Long, Bars);
        mae.ShouldBe(2m);
        mfe.ShouldBe(10m);
    }

    [Fact]
    public void Short_ExcursionsAreMirrored()
    {
        // Short entry 100: adverse is the high (110 -> 10%), favourable is the low (98 -> 2%).
        var (mae, mfe) = PositionBuilder.ComputeMaeMfe(100m, TradeDirection.Short, Bars);
        mae.ShouldBe(10m);
        mfe.ShouldBe(2m);
    }

    [Fact]
    public void ExcursionThatNeverHappened_ClampsToZero()
    {
        // Price never traded below a long entry of 100.
        var (mae, mfe) = PositionBuilder.ComputeMaeMfe(
            100m,
            TradeDirection.Long,
            [(105m, 101m), (107m, 102m)]);
        mae.ShouldBe(0m);
        mfe.ShouldBe(7m);
    }

    [Fact]
    public void EmptyBarsOrInvalidEntry_ReturnsZeros()
    {
        PositionBuilder.ComputeMaeMfe(100m, TradeDirection.Long, [])
            .ShouldBe((0m, 0m));
        PositionBuilder.ComputeMaeMfe(0m, TradeDirection.Long, Bars)
            .ShouldBe((0m, 0m));
    }
}
