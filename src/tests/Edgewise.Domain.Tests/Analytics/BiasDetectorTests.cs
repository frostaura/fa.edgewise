using Edgewise.Domain.Engines.Adherence;
using Edgewise.Domain.Engines.Analytics;
using Shouldly;

namespace Edgewise.Domain.Tests.Analytics;

public class BiasDetectorTests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AsOf = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

    private static BiasTrade Trade(
        string key,
        DateTime entryAt,
        bool isWin,
        long holdingSeconds = 600,
        decimal? risk = null,
        decimal? trigger = null,
        decimal? entryPrice = null,
        TradeDirection direction = TradeDirection.Long)
        => new()
        {
            Key = key,
            EntryAt = entryAt,
            ClosedAt = entryAt.AddSeconds(holdingSeconds),
            IsWin = isWin,
            RRealised = isWin ? 1m : -1m,
            HoldingSeconds = holdingSeconds,
            RiskAmount = risk,
            TriggerPrice = trigger,
            EntryPrice = entryPrice,
            Direction = direction,
        };

    // ---- DispositionRatio -------------------------------------------------

    [Fact]
    public void DispositionRatio_Breach_WhenWinnersHeldMuchLongerThanLosers()
    {
        var trades = new[]
        {
            Trade("w1", T0, isWin: true, holdingSeconds: 3_000),
            Trade("w2", T0.AddHours(1), isWin: true, holdingSeconds: 3_000),
            Trade("l1", T0.AddHours(2), isWin: false, holdingSeconds: 1_000),
            Trade("l2", T0.AddHours(3), isWin: false, holdingSeconds: 1_000),
        };

        var card = BiasDetectors.DispositionRatio(trades);

        card.Code.ShouldBe(BiasDetectors.DispositionCode);
        card.Metric.ShouldBe(3m);
        card.Threshold.ShouldBe(1.5m);
        card.Breached.ShouldBeTrue();
        card.EvidenceKeys.ShouldBe(["w1", "w2"], ignoreOrder: true);
        card.Details["avgHoldWinnersSeconds"].ShouldBe(3_000m);
        card.Details["avgHoldLosersSeconds"].ShouldBe(1_000m);
    }

    [Fact]
    public void DispositionRatio_NoBreach_AtOrBelowThreshold()
    {
        var trades = new[]
        {
            Trade("w1", T0, isWin: true, holdingSeconds: 1_500),
            Trade("l1", T0.AddHours(1), isWin: false, holdingSeconds: 1_000),
        };

        var card = BiasDetectors.DispositionRatio(trades);

        card.Metric.ShouldBe(1.5m);
        card.Breached.ShouldBeFalse(); // strict > comparison
    }

    [Fact]
    public void DispositionRatio_NoLosers_DoesNotBreach()
    {
        var card = BiasDetectors.DispositionRatio([Trade("w1", T0, isWin: true)]);

        card.Metric.ShouldBe(0m);
        card.Breached.ShouldBeFalse();
        card.EvidenceKeys.ShouldBeEmpty();
    }

    // ---- RevengeEntries ---------------------------------------------------

    [Fact]
    public void RevengeEntries_Breach_AtThreeQuickReentriesWithinLookback()
    {
        var trades = new List<BiasTrade>();
        for (var i = 0; i < 3; i++)
        {
            var lossEntry = T0.AddDays(i);
            trades.Add(Trade($"loss{i}", lossEntry, isWin: false, holdingSeconds: 600));
            // Re-entry 5 minutes after the loss closed.
            trades.Add(Trade($"revenge{i}", lossEntry.AddMinutes(15), isWin: i == 0, holdingSeconds: 300));
        }

        var card = BiasDetectors.RevengeEntries(trades, AsOf);

        card.Code.ShouldBe(BiasDetectors.RevengeCode);
        card.Metric.ShouldBe(3m);
        card.Threshold.ShouldBe(3m);
        card.Breached.ShouldBeTrue();
        card.EvidenceKeys.ShouldBe(["revenge0", "revenge1", "revenge2"]);
    }

    [Fact]
    public void RevengeEntries_NoBreach_WithOnlyTwoQuickReentries()
    {
        var trades = new List<BiasTrade>
        {
            Trade("loss0", T0, isWin: false),
            Trade("revenge0", T0.AddMinutes(15), isWin: true, holdingSeconds: 300),
            Trade("loss1", T0.AddDays(1), isWin: false),
            Trade("revenge1", T0.AddDays(1).AddMinutes(15), isWin: true, holdingSeconds: 300),
            // Entered 45 minutes after the loss closed: outside the 30-minute window.
            Trade("loss2", T0.AddDays(2), isWin: false),
            Trade("patient", T0.AddDays(2).AddMinutes(55), isWin: true, holdingSeconds: 300),
        };

        var card = BiasDetectors.RevengeEntries(trades, AsOf);

        card.Metric.ShouldBe(2m);
        card.Breached.ShouldBeFalse();
        card.EvidenceKeys.ShouldBe(["revenge0", "revenge1"]);
    }

    [Fact]
    public void RevengeEntries_OutsideLookback_AreIgnored()
    {
        var old = T0.AddDays(-60);
        var trades = new List<BiasTrade>
        {
            Trade("oldLoss", old, isWin: false),
            Trade("oldRevenge1", old.AddMinutes(15), isWin: false, holdingSeconds: 300),
            Trade("oldRevenge2", old.AddMinutes(25), isWin: false, holdingSeconds: 300),
            Trade("oldRevenge3", old.AddMinutes(29), isWin: false, holdingSeconds: 60),
        };

        var card = BiasDetectors.RevengeEntries(trades, AsOf);

        card.Metric.ShouldBe(0m);
        card.Breached.ShouldBeFalse();
    }

    // ---- SizeCreep --------------------------------------------------------

    [Fact]
    public void SizeCreep_Breach_WhenRiskGrowsAfterThreeWinStreak()
    {
        var trades = new List<BiasTrade>
        {
            Trade("w1", T0, isWin: true, risk: 100m),
            Trade("w2", T0.AddHours(1), isWin: true, risk: 100m),
            Trade("w3", T0.AddHours(2), isWin: true, risk: 100m),
            // Entered after three consecutive wins with 1.5x the baseline risk.
            Trade("creep", T0.AddHours(3), isWin: false, risk: 150m),
        };

        var card = BiasDetectors.SizeCreep(trades);

        card.Code.ShouldBe(BiasDetectors.SizeCreepCode);
        card.Metric.ShouldBe(1.5m);
        card.Threshold.ShouldBe(1.25m);
        card.Breached.ShouldBeTrue();
        card.EvidenceKeys.ShouldBe(["creep"]);
        card.Details["avgRiskPostStreak"].ShouldBe(150m);
        card.Details["avgRiskBaseline"].ShouldBe(100m);
    }

    [Fact]
    public void SizeCreep_NoBreach_WhenPostStreakRiskIsFlat()
    {
        var trades = new List<BiasTrade>
        {
            Trade("w1", T0, isWin: true, risk: 100m),
            Trade("w2", T0.AddHours(1), isWin: true, risk: 100m),
            Trade("w3", T0.AddHours(2), isWin: true, risk: 100m),
            Trade("steady", T0.AddHours(3), isWin: false, risk: 110m),
        };

        var card = BiasDetectors.SizeCreep(trades);

        card.Metric.ShouldBe(1.1m);
        card.Breached.ShouldBeFalse();
    }

    [Fact]
    public void SizeCreep_StreakBrokenByALoss_DoesNotFlagNextTrade()
    {
        var trades = new List<BiasTrade>
        {
            Trade("w1", T0, isWin: true, risk: 100m),
            Trade("w2", T0.AddHours(1), isWin: true, risk: 100m),
            Trade("l1", T0.AddHours(2), isWin: false, risk: 100m),
            Trade("big", T0.AddHours(3), isWin: true, risk: 200m),
        };

        var card = BiasDetectors.SizeCreep(trades);

        card.Metric.ShouldBe(0m);
        card.Breached.ShouldBeFalse();
        card.EvidenceKeys.ShouldBeEmpty();
    }

    // ---- FomoChase --------------------------------------------------------

    [Fact]
    public void FomoChase_Breach_AtThreeChasedEntries()
    {
        var trades = new List<BiasTrade>
        {
            // Long chases: entered 1% above the trigger (threshold 0.5%).
            Trade("chase1", T0, isWin: true, trigger: 100m, entryPrice: 101m),
            Trade("chase2", T0.AddDays(1), isWin: false, trigger: 200m, entryPrice: 202m),
            // Short chase: entered below the trigger.
            Trade("chase3", T0.AddDays(2), isWin: false, trigger: 100m, entryPrice: 99m,
                direction: TradeDirection.Short),
            // Disciplined: filled exactly at trigger.
            Trade("ok", T0.AddDays(3), isWin: true, trigger: 100m, entryPrice: 100m),
        };

        var card = BiasDetectors.FomoChase(trades, AsOf, chaseThresholdPct: 0.005m);

        card.Code.ShouldBe(BiasDetectors.FomoChaseCode);
        card.Metric.ShouldBe(3m);
        card.Threshold.ShouldBe(3m);
        card.Breached.ShouldBeTrue();
        card.EvidenceKeys.ShouldBe(["chase1", "chase2", "chase3"]);
        card.Details["maxChasePct"].ShouldBe(0.01m);
    }

    [Fact]
    public void FomoChase_NoBreach_BelowCount()
    {
        var trades = new List<BiasTrade>
        {
            Trade("chase1", T0, isWin: true, trigger: 100m, entryPrice: 102m),
            Trade("ok1", T0.AddDays(1), isWin: true, trigger: 100m, entryPrice: 100.2m),
            Trade("noPrices", T0.AddDays(2), isWin: true),
        };

        var card = BiasDetectors.FomoChase(trades, AsOf, chaseThresholdPct: 0.005m);

        card.Metric.ShouldBe(1m);
        card.Breached.ShouldBeFalse();
        card.EvidenceKeys.ShouldBe(["chase1"]);
    }

    [Fact]
    public void FomoChase_FavourableFill_IsNotAChase()
    {
        var trades = new List<BiasTrade>
        {
            // Long filled below trigger: better than planned.
            Trade("good", T0, isWin: true, trigger: 100m, entryPrice: 99m),
        };

        var card = BiasDetectors.FomoChase(trades, AsOf, chaseThresholdPct: 0.005m);

        card.Metric.ShouldBe(0m);
        card.EvidenceKeys.ShouldBeEmpty();
    }
}
