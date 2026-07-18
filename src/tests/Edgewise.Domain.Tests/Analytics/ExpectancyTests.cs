using Edgewise.Domain.Engines.Analytics;
using Shouldly;
using static Edgewise.Domain.Tests.Analytics.AnalyticsTestData;

namespace Edgewise.Domain.Tests.Analytics;

public class ExpectancyTests
{
    [Fact]
    public void GroupedExpectancy_ComputesPerGroupStatistics()
    {
        // Setup A: 2 wins of +2R, 2 losses of -1R -> winRate 0.5, expectancy 0.5R.
        // Setup B: 1 win of +1R -> winRate 1, expectancy 1R.
        var trades = new[]
        {
            Trade(T0, r: 2m, isWin: true, setup: "A"),
            Trade(T0.AddHours(1), r: 2m, isWin: true, setup: "A"),
            Trade(T0.AddHours(2), r: -1m, setup: "A"),
            Trade(T0.AddHours(3), r: -1m, setup: "A"),
            Trade(T0.AddHours(4), r: 1m, isWin: true, setup: "B"),
        };

        var report = RAnalyticsEngine.GroupedExpectancy(trades, t => t.SetupTag);

        report.Groups.Count.ShouldBe(2);
        var a = report.Groups.Single(g => g.Key == "A");
        a.N.ShouldBe(4);
        a.WinRate.ShouldBe(0.5m);
        a.AvgWinR.ShouldBe(2m);
        a.AvgLossR.ShouldBe(-1m);
        a.ExpectancyR.ShouldBe(0.5m);
        a.LowSample.ShouldBeTrue();

        var b = report.Groups.Single(g => g.Key == "B");
        b.N.ShouldBe(1);
        b.WinRate.ShouldBe(1m);
        b.ExpectancyR.ShouldBe(1m);
    }

    [Fact]
    public void LowSampleFlag_ClearsAtThirtyTrades()
    {
        var small = Enumerable.Range(0, 29).Select(i => Trade(T0.AddMinutes(i), r: 1m, isWin: true));
        var large = Enumerable.Range(0, 30).Select(i => Trade(T0.AddMinutes(i), r: 1m, isWin: true));

        RAnalyticsEngine.GroupedExpectancy(small, t => t.SetupTag)
            .Groups.Single().LowSample.ShouldBeTrue();
        RAnalyticsEngine.GroupedExpectancy(large, t => t.SetupTag)
            .Groups.Single().LowSample.ShouldBeFalse();
    }

    [Fact]
    public void TradesWithoutRealisedR_CountForWinRateButNotForRAverages()
    {
        var trades = new[]
        {
            Trade(T0, r: 3m, isWin: true),
            Trade(T0.AddHours(1), r: null, isWin: true),
            Trade(T0.AddHours(2), r: -1m),
        };

        var group = RAnalyticsEngine.GroupedExpectancy(trades, t => t.SetupTag).Groups.Single();

        group.N.ShouldBe(3);
        group.WinRate.ShouldBe(2m / 3m, 0.0001m);
        group.AvgWinR.ShouldBe(3m); // the R-less win is excluded from the average
        group.AvgLossR.ShouldBe(-1m);
    }

    [Fact]
    public void Wilson95_MatchesKnownValues_TenTrials()
    {
        // p-hat = 0.5, n = 10, z = 1.96 -> [0.2366, 0.7634].
        var (lo, hi) = RAnalyticsEngine.Wilson95(5, 10);

        lo.ShouldBe(0.2366, 0.001);
        hi.ShouldBe(0.7634, 0.001);
    }

    [Fact]
    public void Wilson95_MatchesKnownValues_HundredTrials()
    {
        // p-hat = 0.5, n = 100 -> [0.4038, 0.5962].
        var (lo, hi) = RAnalyticsEngine.Wilson95(50, 100);

        lo.ShouldBe(0.4038, 0.001);
        hi.ShouldBe(0.5962, 0.001);
    }

    [Fact]
    public void Wilson95_ExtremeProportions_StayInsideUnitInterval()
    {
        var (lo0, hi0) = RAnalyticsEngine.Wilson95(0, 10);
        lo0.ShouldBe(0d, 1e-9);
        hi0.ShouldBe(0.2775, 0.001); // known value for 0/10

        var (lo1, hi1) = RAnalyticsEngine.Wilson95(10, 10);
        lo1.ShouldBe(0.7225, 0.001);
        hi1.ShouldBe(1d, 1e-9);
    }

    [Fact]
    public void Wilson95_EmptySample_ReturnsZeroInterval()
    {
        RAnalyticsEngine.Wilson95(0, 0).ShouldBe((0d, 0d));
    }

    [Fact]
    public void GroupedExpectancy_EmptyInput_ReturnsNoGroups()
    {
        RAnalyticsEngine.GroupedExpectancy(Array.Empty<TradeStat>(), t => t.SetupTag)
            .Groups.ShouldBeEmpty();
    }
}
