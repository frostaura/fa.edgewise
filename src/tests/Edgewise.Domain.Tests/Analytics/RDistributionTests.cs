using Edgewise.Domain.Engines.Analytics;
using Shouldly;
using static Edgewise.Domain.Tests.Analytics.AnalyticsTestData;

namespace Edgewise.Domain.Tests.Analytics;

public class RDistributionTests
{
    [Fact]
    public void BinsRValues_IntoHalfRBuckets()
    {
        var trades = new[]
        {
            Trade(T0, r: -1.2m),
            Trade(T0, r: -0.5m), // boundary: goes into [-0.5, 0)
            Trade(T0, r: 0.1m),
            Trade(T0, r: 0.4m),
            Trade(T0, r: 2.0m),
        };

        var report = RAnalyticsEngine.RDistribution(trades);

        report.BinSize.ShouldBe(0.5m);
        report.N.ShouldBe(5);
        // Contiguous from [-1.5, -1) up to [2, 2.5).
        report.Bins.First().From.ShouldBe(-1.5m);
        report.Bins.Last().From.ShouldBe(2.0m);
        report.Bins.Count.ShouldBe(8);

        CountAt(report, -1.5m).ShouldBe(1);
        CountAt(report, -1.0m).ShouldBe(0);
        CountAt(report, -0.5m).ShouldBe(1);
        CountAt(report, 0.0m).ShouldBe(2);
        CountAt(report, 0.5m).ShouldBe(0);
        CountAt(report, 2.0m).ShouldBe(1);
        report.Bins.Sum(b => b.Count).ShouldBe(5);
    }

    [Fact]
    public void CustomBinSize_IsRespected()
    {
        var trades = new[] { Trade(T0, r: 0.9m), Trade(T0, r: 1.0m) };

        var report = RAnalyticsEngine.RDistribution(trades, binSize: 1m);

        report.Bins.Count.ShouldBe(2);
        CountAt(report, 0m).ShouldBe(1);
        CountAt(report, 1m).ShouldBe(1);
    }

    [Fact]
    public void TradesWithoutR_AreIgnored()
    {
        var trades = new[] { Trade(T0, r: null), Trade(T0, r: 1m) };

        var report = RAnalyticsEngine.RDistribution(trades);

        report.N.ShouldBe(1);
        report.Bins.Sum(b => b.Count).ShouldBe(1);
    }

    [Fact]
    public void EmptyInput_YieldsEmptyHistogram()
    {
        var report = RAnalyticsEngine.RDistribution([]);

        report.N.ShouldBe(0);
        report.Bins.ShouldBeEmpty();
    }

    [Fact]
    public void NonPositiveBinSize_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => RAnalyticsEngine.RDistribution([], 0m));
        Should.Throw<ArgumentOutOfRangeException>(() => RAnalyticsEngine.RDistribution([], -0.5m));
    }

    private static int CountAt(RDistributionReport report, decimal from) =>
        report.Bins.Single(b => b.From == from).Count;
}
