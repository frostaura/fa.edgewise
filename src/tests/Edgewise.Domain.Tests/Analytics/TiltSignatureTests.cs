using Edgewise.Domain.Engines.Analytics;
using Shouldly;
using static Edgewise.Domain.Tests.Analytics.AnalyticsTestData;

namespace Edgewise.Domain.Tests.Analytics;

public class TiltSignatureTests
{
    [Fact]
    public void ClassifiesTradesEnteredWithinSixtyMinutesAfterALoss()
    {
        var loss = Trade(T0, r: -1m, isWin: false, holdingSeconds: 600);
        // Entry T0 + 30min -> after-loss.
        var revenge = Trade(T0.AddMinutes(40), r: 1m, isWin: true, holdingSeconds: 600);
        // Entry exactly T0 + 60min -> NOT after-loss (strict < 60).
        var calm = Trade(T0.AddMinutes(70), r: 1m, isWin: true, holdingSeconds: 600);

        var report = RAnalyticsEngine.TiltSignature([loss, revenge, calm]);

        report.NAfterLoss.ShouldBe(1);
        report.ExpectancyAfterLoss.ShouldBe(1m);
        report.BaselineExpectancy.ShouldBe(0m); // (-1 + 1) / 2
        report.DeltaR.ShouldBe(1m);
        report.IsSignificant.ShouldBeFalse(); // far below the n >= 10 requirement
    }

    [Fact]
    public void StrongNegativeAfterLossPerformance_IsSignificant()
    {
        var report = RAnalyticsEngine.TiltSignature(SyntheticTilt(afterLossMeanR: -1m));

        report.NAfterLoss.ShouldBe(10);
        report.ExpectancyAfterLoss.ShouldBe(-1m, 0.001m);
        report.BaselineExpectancy.ShouldBe(0m, 0.001m);
        report.DeltaR.ShouldBe(-1m, 0.001m);
        report.IsSignificant.ShouldBeTrue();
    }

    [Fact]
    public void AfterLossPerformanceMatchingBaseline_IsNotSignificant()
    {
        var report = RAnalyticsEngine.TiltSignature(SyntheticTilt(afterLossMeanR: 0m));

        report.NAfterLoss.ShouldBe(10);
        report.DeltaR.ShouldBe(0m, 0.15m);
        report.IsSignificant.ShouldBeFalse();
    }

    [Fact]
    public void FewerThanTenAfterLossTrades_NeverSignificant()
    {
        var report = RAnalyticsEngine.TiltSignature(SyntheticTilt(afterLossMeanR: -1m, pairs: 5));

        report.NAfterLoss.ShouldBe(5);
        report.DeltaR.ShouldBeLessThan(0m);
        report.IsSignificant.ShouldBeFalse();
    }

    [Fact]
    public void EmptyInput_ReturnsZeroedReport()
    {
        var report = RAnalyticsEngine.TiltSignature([]);

        report.NAfterLoss.ShouldBe(0);
        report.ExpectancyAfterLoss.ShouldBe(0m);
        report.BaselineExpectancy.ShouldBe(0m);
        report.IsSignificant.ShouldBeFalse();
    }

    [Fact]
    public void TradesWithoutRealisedR_AreExcludedFromBothSets()
    {
        var loss = Trade(T0, r: -1m, isWin: false, holdingSeconds: 600);
        var noR = Trade(T0.AddMinutes(20), r: null, isWin: true, holdingSeconds: 300);

        var report = RAnalyticsEngine.TiltSignature([loss, noR]);

        report.NAfterLoss.ShouldBe(0);
        report.BaselineExpectancy.ShouldBe(-1m);
    }

    /// <summary>
    /// Builds pairs of (losing trade, quick follow-up trade entered 10 minutes after the loss)
    /// plus one distant winner per pair. Baseline = losers (mean -1R) + winners (mean +1R),
    /// baseline expectancy ~0. After-loss trades get R alternating around
    /// <paramref name="afterLossMeanR"/> (+/- 0.1R).
    /// </summary>
    private static List<TradeStat> SyntheticTilt(decimal afterLossMeanR, int pairs = 10)
    {
        var trades = new List<TradeStat>();
        for (var i = 0; i < pairs; i++)
        {
            var lossClose = T0.AddHours(i * 4);
            var jitter = i % 2 == 0 ? 0.1m : -0.1m;

            // Losing trade (baseline): entered well clear of any prior loss.
            trades.Add(Trade(lossClose, r: -1m + jitter, isWin: false, holdingSeconds: 600));

            // After-loss trade: entry 10 minutes after the loss closed, held 5 minutes.
            var afterLossR = afterLossMeanR + jitter;
            trades.Add(Trade(lossClose.AddMinutes(15), r: afterLossR, isWin: afterLossR > 0m, holdingSeconds: 300));

            // Distant winner (baseline): entered 2 hours after the loss closed.
            trades.Add(Trade(lossClose.AddHours(2).AddMinutes(10), r: 1m + jitter, isWin: true, holdingSeconds: 600));
        }

        return trades;
    }
}
