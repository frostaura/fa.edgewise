using Edgewise.Domain.Engines.Positions;
using Shouldly;

namespace Edgewise.Domain.Tests.Positions;

internal static class TestMoney
{
    /// <summary>Currency with 2 decimal places; exact for the test inputs used here.</summary>
    public static long ToMinor(decimal d) =>
        (long)decimal.Round(d * 100m, 0, MidpointRounding.AwayFromZero);
}

internal static class TradeAssert
{
    /// <summary>Structural deep equality over the full TradeAggregate list, including sequences.</summary>
    public static void ShouldDeepEqual(
        IReadOnlyList<TradeAggregate> actual,
        IReadOnlyList<TradeAggregate> expected)
    {
        actual.Count.ShouldBe(expected.Count);
        for (var i = 0; i < actual.Count; i++)
        {
            var a = actual[i];
            var e = expected[i];
            a.TradeKey.ShouldBe(e.TradeKey);
            a.Direction.ShouldBe(e.Direction);
            a.OpenedAt.ShouldBe(e.OpenedAt);
            a.ClosedAt.ShouldBe(e.ClosedAt);
            a.Status.ShouldBe(e.Status);
            a.Qty.ShouldBe(e.Qty);
            a.AvgEntryPrice.ShouldBe(e.AvgEntryPrice);
            a.AvgExitPrice.ShouldBe(e.AvgExitPrice);
            a.RealisedPnlMinor.ShouldBe(e.RealisedPnlMinor);
            a.FeesMinor.ShouldBe(e.FeesMinor);
            a.FundingMinor.ShouldBe(e.FundingMinor);
            a.FillKeys.ShouldBe(e.FillKeys);
            a.RemainingQty.ShouldBe(e.RemainingQty);
            a.OpenLots.ShouldBe(e.OpenLots);
        }
    }
}
