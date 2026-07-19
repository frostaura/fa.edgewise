using Edgewise.Domain.Engines.Positions;
using Shouldly;

namespace Edgewise.Domain.Tests.Positions;

public class PositionBuilderScenarioTests
{
    private const string Instrument = "ES";
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 30, 0, DateTimeKind.Utc);

    private static FillEvent F(
        string key,
        int minute,
        Side side,
        decimal qty,
        decimal px,
        long fee = 0,
        long funding = 0,
        string? pin = null) =>
        new(key, T0.AddMinutes(minute), side, qty, px, fee, funding, pin);

    private static IReadOnlyList<TradeAggregate> Build(params FillEvent[] fills) =>
        PositionBuilder.Build(Instrument, fills, TestMoney.ToMinor);

    [Fact]
    public void SingleOpenFill_ProducesOneOpenLongTrade()
    {
        var trades = Build(F("f1", 0, Side.Buy, 10m, 100m, fee: 50));

        var t = trades.ShouldHaveSingleItem();
        t.TradeKey.ShouldBe("ES:1");
        t.Direction.ShouldBe(TradeDirection.Long);
        t.Status.ShouldBe(TradeStatus.Open);
        t.OpenedAt.ShouldBe(T0);
        t.ClosedAt.ShouldBeNull();
        t.Qty.ShouldBe(10m);
        t.AvgEntryPrice.ShouldBe(100m);
        t.AvgExitPrice.ShouldBeNull();
        t.RealisedPnlMinor.ShouldBe(0);
        t.FeesMinor.ShouldBe(50);
        t.FundingMinor.ShouldBe(0);
        t.RemainingQty.ShouldBe(10m);
        t.FillKeys.ShouldBe(new[] { "f1" });
        t.OpenLots.ShouldBe(new[] { new OpenLot(10m, 100m) });
    }

    [Fact]
    public void LongRoundTrip_Profit()
    {
        // Buy 10 @ 100, Sell 10 @ 110: realised = (110 - 100) * 10 = 100.00 => 10000 minor.
        var trades = Build(
            F("b1", 0, Side.Buy, 10m, 100m, fee: 25),
            F("s1", 5, Side.Sell, 10m, 110m, fee: 25));

        var t = trades.ShouldHaveSingleItem();
        t.Direction.ShouldBe(TradeDirection.Long);
        t.Status.ShouldBe(TradeStatus.Closed);
        t.ClosedAt.ShouldBe(T0.AddMinutes(5));
        t.AvgEntryPrice.ShouldBe(100m);
        t.AvgExitPrice.ShouldBe(110m);
        t.RealisedPnlMinor.ShouldBe(10_000);
        t.FeesMinor.ShouldBe(50);
        t.RemainingQty.ShouldBe(0m);
        t.OpenLots.ShouldBeEmpty();
        t.FillKeys.ShouldBe(new[] { "b1", "s1" });
    }

    [Fact]
    public void LongRoundTrip_Loss()
    {
        // Realised = (95 - 100) * 10 = -50.00 => -5000 minor.
        var trades = Build(
            F("b1", 0, Side.Buy, 10m, 100m),
            F("s1", 5, Side.Sell, 10m, 95m));

        var t = trades.ShouldHaveSingleItem();
        t.Status.ShouldBe(TradeStatus.Closed);
        t.RealisedPnlMinor.ShouldBe(-5_000);
    }

    [Fact]
    public void ShortRoundTrip_Profit()
    {
        // Short: realised = (exit - entry) * qty * (-1) = (90 - 100) * 10 * -1 = +100.00.
        var trades = Build(
            F("s1", 0, Side.Sell, 10m, 100m),
            F("b1", 5, Side.Buy, 10m, 90m));

        var t = trades.ShouldHaveSingleItem();
        t.Direction.ShouldBe(TradeDirection.Short);
        t.Status.ShouldBe(TradeStatus.Closed);
        t.AvgEntryPrice.ShouldBe(100m);
        t.AvgExitPrice.ShouldBe(90m);
        t.RealisedPnlMinor.ShouldBe(10_000);
    }

    [Fact]
    public void ShortRoundTrip_Loss()
    {
        // (105 - 100) * 10 * -1 = -50.00 => -5000 minor.
        var trades = Build(
            F("s1", 0, Side.Sell, 10m, 100m),
            F("b1", 5, Side.Buy, 10m, 105m));

        var t = trades.ShouldHaveSingleItem();
        t.Direction.ShouldBe(TradeDirection.Short);
        t.RealisedPnlMinor.ShouldBe(-5_000);
    }

    [Fact]
    public void PartialExit_TradeStaysOpenWithRealisedPnl()
    {
        // Buy 10 @ 100, Sell 4 @ 110: realised = 10 * 4 = 40.00 => 4000, remaining 6.
        var trades = Build(
            F("b1", 0, Side.Buy, 10m, 100m),
            F("s1", 5, Side.Sell, 4m, 110m));

        var t = trades.ShouldHaveSingleItem();
        t.Status.ShouldBe(TradeStatus.Open);
        t.ClosedAt.ShouldBeNull();
        t.Qty.ShouldBe(10m);
        t.RemainingQty.ShouldBe(6m);
        t.RealisedPnlMinor.ShouldBe(4_000);
        t.AvgExitPrice.ShouldBe(110m);
        t.OpenLots.ShouldBe(new[] { new OpenLot(6m, 100m) });
    }

    [Fact]
    public void MultipleScaleIns_EntryVwapIsHandComputed()
    {
        // VWAP = (10*100 + 20*110 + 10*120) / 40 = (1000 + 2200 + 1200) / 40 = 4400 / 40 = 110.
        var trades = Build(
            F("b1", 0, Side.Buy, 10m, 100m),
            F("b2", 1, Side.Buy, 20m, 110m),
            F("b3", 2, Side.Buy, 10m, 120m));

        var t = trades.ShouldHaveSingleItem();
        t.Qty.ShouldBe(40m);
        t.AvgEntryPrice.ShouldBe(110m);
        t.RemainingQty.ShouldBe(40m);
        t.OpenLots.ShouldBe(new[]
        {
            new OpenLot(10m, 100m),
            new OpenLot(20m, 110m),
            new OpenLot(10m, 120m),
        });
    }

    [Fact]
    public void ScaleOutFifo_RealisesAgainstThreeLotsInOrder()
    {
        // Lots: 5 @ 100, 5 @ 110, 5 @ 120.
        // Sell 8 @ 115 consumes lot1 fully and 3 of lot2:
        //   (115-100)*5 = 75; (115-110)*3 = 15; realisation #1 = +90.00 => 9000 minor.
        // Sell 7 @ 105 consumes 2 of lot2 and lot3 fully:
        //   (105-110)*2 = -10; (105-120)*5 = -75; realisation #2 = -85.00 => -8500 minor.
        // Total realised = 500 minor. Exit VWAP = (8*115 + 7*105)/15 = 1655/15 = 110.333...
        var conversions = new List<decimal>();
        long SpyToMinor(decimal d)
        {
            conversions.Add(d);
            return TestMoney.ToMinor(d);
        }

        var trades = PositionBuilder.Build(
            Instrument,
            new[]
            {
                F("b1", 0, Side.Buy, 5m, 100m),
                F("b2", 1, Side.Buy, 5m, 110m),
                F("b3", 2, Side.Buy, 5m, 120m),
                F("s1", 3, Side.Sell, 8m, 115m),
                F("s2", 4, Side.Sell, 7m, 105m),
            },
            SpyToMinor);

        var t = trades.ShouldHaveSingleItem();
        t.Status.ShouldBe(TradeStatus.Closed);
        t.RealisedPnlMinor.ShouldBe(500);
        t.AvgExitPrice!.Value.ShouldBe(1655m / 15m, 0.0000001m);

        // Money math stays decimal and is converted exactly once per realisation event.
        conversions.ShouldBe(new[] { 90m, -85m });
    }

    [Fact]
    public void FlipLongToShort_ClosesTradeAndOpensOppositeWithProRataCosts()
    {
        // Buy 10 @ 100 (fee 100), Sell 15 @ 110 (fee 150, funding 30).
        // Closing 10 realises (110-100)*10 = +100.00 => 10000 minor.
        // Flip fractions: closed 10/15 = 2/3 -> fee 100, funding 20; residual gets 50 and 10.
        var trades = Build(
            F("b1", 0, Side.Buy, 10m, 100m, fee: 100),
            F("s1", 5, Side.Sell, 15m, 110m, fee: 150, funding: 30));

        trades.Count.ShouldBe(2);

        var closed = trades[0];
        closed.TradeKey.ShouldBe("ES:1");
        closed.Direction.ShouldBe(TradeDirection.Long);
        closed.Status.ShouldBe(TradeStatus.Closed);
        closed.ClosedAt.ShouldBe(T0.AddMinutes(5));
        closed.Qty.ShouldBe(10m);
        closed.AvgEntryPrice.ShouldBe(100m);
        closed.AvgExitPrice.ShouldBe(110m);
        closed.RealisedPnlMinor.ShouldBe(10_000);
        closed.FeesMinor.ShouldBe(200);
        closed.FundingMinor.ShouldBe(20);
        closed.FillKeys.ShouldBe(new[] { "b1", "s1" });

        var opened = trades[1];
        opened.TradeKey.ShouldBe("ES:2");
        opened.Direction.ShouldBe(TradeDirection.Short);
        opened.Status.ShouldBe(TradeStatus.Open);
        opened.OpenedAt.ShouldBe(T0.AddMinutes(5));
        opened.Qty.ShouldBe(5m);
        opened.AvgEntryPrice.ShouldBe(110m);
        opened.RemainingQty.ShouldBe(5m);
        opened.FeesMinor.ShouldBe(50);
        opened.FundingMinor.ShouldBe(10);
        opened.FillKeys.ShouldBe(new[] { "s1" });

        // Pro-rata split conserves totals.
        (closed.FeesMinor + opened.FeesMinor).ShouldBe(250);
        (closed.FundingMinor + opened.FundingMinor).ShouldBe(30);
    }

    [Fact]
    public void FlipShortToLong_RealisesAndSplitsOddFeeWithoutLosingAUnit()
    {
        // Sell 10 @ 100 (fee 90), Buy 16 @ 95 (fee 101).
        // Closing 10 realises (95-100)*10*-1 = +50.00 => 5000 minor.
        // Fee split: closed fraction 10/16 = 0.625 -> round(101 * 0.625) = round(63.125) = 63;
        // residual trade gets 101 - 63 = 38. Totals conserved.
        var trades = Build(
            F("s1", 0, Side.Sell, 10m, 100m, fee: 90),
            F("b1", 5, Side.Buy, 16m, 95m, fee: 101));

        trades.Count.ShouldBe(2);

        var closed = trades[0];
        closed.Direction.ShouldBe(TradeDirection.Short);
        closed.Status.ShouldBe(TradeStatus.Closed);
        closed.RealisedPnlMinor.ShouldBe(5_000);
        closed.FeesMinor.ShouldBe(90 + 63);

        var opened = trades[1];
        opened.TradeKey.ShouldBe("ES:2");
        opened.Direction.ShouldBe(TradeDirection.Long);
        opened.Qty.ShouldBe(6m);
        opened.AvgEntryPrice.ShouldBe(95m);
        opened.FeesMinor.ShouldBe(38);

        (closed.FeesMinor + opened.FeesMinor).ShouldBe(90 + 101);
    }

    [Fact]
    public void FundingAccruesOnTheActiveTrade()
    {
        var trades = Build(
            F("b1", 0, Side.Buy, 10m, 100m, fee: 10, funding: 5),
            F("b2", 1, Side.Buy, 5m, 100m, fee: 10, funding: 7),
            F("s1", 2, Side.Sell, 15m, 100m, fee: 10, funding: 9));

        var t = trades.ShouldHaveSingleItem();
        t.Status.ShouldBe(TradeStatus.Closed);
        t.RealisedPnlMinor.ShouldBe(0);
        t.FeesMinor.ShouldBe(30);
        t.FundingMinor.ShouldBe(21);
    }

    [Fact]
    public void ZeroQtyFills_AreIgnored()
    {
        var meaningful = new[]
        {
            F("b1", 0, Side.Buy, 10m, 100m),
            F("s1", 5, Side.Sell, 10m, 105m),
        };
        var withNoise = new[]
        {
            meaningful[0],
            F("z1", 1, Side.Buy, 0m, 999m, fee: 123),
            F("z2", 2, Side.Sell, -3m, 999m, fee: 456),
            meaningful[1],
        };

        TradeAssert.ShouldDeepEqual(Build(withNoise), Build(meaningful));
    }

    [Fact]
    public void OutOfOrderTimestamps_AreSortedBeforeProcessing()
    {
        var shuffled = new[]
        {
            F("s1", 10, Side.Sell, 5m, 110m),
            F("b2", 5, Side.Buy, 2m, 104m),
            F("b1", 0, Side.Buy, 3m, 100m),
        };
        var sorted = new[]
        {
            F("b1", 0, Side.Buy, 3m, 100m),
            F("b2", 5, Side.Buy, 2m, 104m),
            F("s1", 10, Side.Sell, 5m, 110m),
        };

        TradeAssert.ShouldDeepEqual(Build(shuffled), Build(sorted));
    }

    [Fact]
    public void EqualTimestamps_KeepInputOrder_StableSort()
    {
        // Buy then Sell at the same instant: stable sort must process the buy first,
        // producing a closed long trade rather than opening a short.
        var trades = Build(
            F("b1", 0, Side.Buy, 5m, 100m),
            F("s1", 0, Side.Sell, 5m, 110m));

        var t = trades.ShouldHaveSingleItem();
        t.Direction.ShouldBe(TradeDirection.Long);
        t.Status.ShouldBe(TradeStatus.Closed);
        t.RealisedPnlMinor.ShouldBe(5_000);
    }

    [Fact]
    public void PinnedSplit_TwoConcurrentTradesOnTheSameInstrument()
    {
        var trades = Build(
            F("b1", 0, Side.Buy, 10m, 100m, pin: "manual-A"),
            F("b2", 1, Side.Buy, 5m, 101m),
            F("s1", 2, Side.Sell, 10m, 105m, pin: "manual-A"),
            F("s2", 3, Side.Sell, 5m, 103m));

        trades.Count.ShouldBe(2);

        var pinned = trades[0];
        pinned.TradeKey.ShouldBe("manual-A");
        pinned.Direction.ShouldBe(TradeDirection.Long);
        pinned.Status.ShouldBe(TradeStatus.Closed);
        pinned.Qty.ShouldBe(10m);
        pinned.AvgEntryPrice.ShouldBe(100m);
        pinned.AvgExitPrice.ShouldBe(105m);
        pinned.RealisedPnlMinor.ShouldBe(5_000);
        pinned.FillKeys.ShouldBe(new[] { "b1", "s1" });

        var fifo = trades[1];
        fifo.TradeKey.ShouldBe("ES:1");
        fifo.Status.ShouldBe(TradeStatus.Closed);
        fifo.Qty.ShouldBe(5m);
        fifo.AvgEntryPrice.ShouldBe(101m);
        fifo.RealisedPnlMinor.ShouldBe(1_000);
        fifo.FillKeys.ShouldBe(new[] { "b2", "s2" });

        // The two trades overlap in time: genuinely concurrent on one instrument.
        pinned.OpenedAt.ShouldBeLessThan(fifo.OpenedAt);
        pinned.ClosedAt!.Value.ShouldBeGreaterThan(fifo.OpenedAt);
    }

    [Fact]
    public void PinnedAndFifoMix_PinnedGroupFlipsIndependentlyOfFifoFlow()
    {
        var trades = Build(
            F("p1", 0, Side.Buy, 5m, 100m, pin: "P"),
            F("u1", 1, Side.Buy, 4m, 102m),
            F("p2", 2, Side.Sell, 8m, 110m, pin: "P"),
            F("u2", 3, Side.Sell, 4m, 104m));

        trades.Count.ShouldBe(3);
        trades.Select(t => t.TradeKey).ShouldBe(new[] { "P", "ES:1", "P:2" });

        var pinnedClosed = trades[0];
        pinnedClosed.Status.ShouldBe(TradeStatus.Closed);
        pinnedClosed.Direction.ShouldBe(TradeDirection.Long);
        pinnedClosed.RealisedPnlMinor.ShouldBe(5_000); // (110-100)*5
        pinnedClosed.FillKeys.ShouldBe(new[] { "p1", "p2" });

        var fifoClosed = trades[1];
        fifoClosed.Status.ShouldBe(TradeStatus.Closed);
        fifoClosed.RealisedPnlMinor.ShouldBe(800); // (104-102)*4
        fifoClosed.FillKeys.ShouldBe(new[] { "u1", "u2" });

        var pinnedFlip = trades[2];
        pinnedFlip.Direction.ShouldBe(TradeDirection.Short);
        pinnedFlip.Status.ShouldBe(TradeStatus.Open);
        pinnedFlip.Qty.ShouldBe(3m);
        pinnedFlip.AvgEntryPrice.ShouldBe(110m);
        pinnedFlip.RemainingQty.ShouldBe(3m);
        pinnedFlip.FillKeys.ShouldBe(new[] { "p2" });
    }

    [Fact]
    public void Deterministic_SameInputSameOutput()
    {
        var fills = new[]
        {
            F("b1", 0, Side.Buy, 10m, 100m, fee: 10),
            F("s1", 1, Side.Sell, 15m, 110m, fee: 15, funding: 7),
            F("b2", 2, Side.Buy, 5m, 108m, fee: 5, pin: "X"),
            F("s2", 3, Side.Sell, 5m, 112m, fee: 5, pin: "X"),
        };

        TradeAssert.ShouldDeepEqual(Build(fills), Build(fills));
    }
}
