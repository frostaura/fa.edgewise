using Edgewise.Domain.Engines.Analytics;
using Shouldly;

namespace Edgewise.Domain.Tests.Analytics;

public class DrawdownTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<EquityPoint> Series(params long[] equities) =>
        equities.Select((e, i) => new EquityPoint(T0.AddDays(i), e)).ToList();

    [Fact]
    public void KnownSeries_ProducesExpectedDrawdowns()
    {
        // Equity: 100, 120, 90, 110, 80, 100
        // Peak:   100, 120, 120, 120, 120, 120
        // DD:     0,   0,   30,  10,  40,  20
        var report = RAnalyticsEngine.DrawdownCurve(Series(100, 120, 90, 110, 80, 100));

        report.Series.Select(p => p.PeakMinor).ShouldBe([100L, 120, 120, 120, 120, 120]);
        report.Series.Select(p => p.DrawdownMinor).ShouldBe([0L, 0, 30, 10, 40, 20]);
        report.MaxDrawdownMinor.ShouldBe(40);
        report.CurrentDrawdownMinor.ShouldBe(20);
    }

    [Fact]
    public void MonotonicallyRisingEquity_HasZeroDrawdown()
    {
        var report = RAnalyticsEngine.DrawdownCurve(Series(100, 110, 125, 200));

        report.MaxDrawdownMinor.ShouldBe(0);
        report.CurrentDrawdownMinor.ShouldBe(0);
        report.Series.ShouldAllBe(p => p.DrawdownMinor == 0);
    }

    [Fact]
    public void EndingAtNewLow_CurrentEqualsMaxDrawdown()
    {
        var report = RAnalyticsEngine.DrawdownCurve(Series(100, 150, 120, 110));

        report.MaxDrawdownMinor.ShouldBe(40);
        report.CurrentDrawdownMinor.ShouldBe(40);
    }

    [Fact]
    public void UnorderedInput_IsSortedByTime()
    {
        var points = new[]
        {
            new EquityPoint(T0.AddDays(2), 90),
            new EquityPoint(T0, 100),
            new EquityPoint(T0.AddDays(1), 120),
        };

        var report = RAnalyticsEngine.DrawdownCurve(points);

        report.Series.Select(p => p.EquityMinor).ShouldBe([100L, 120, 90]);
        report.MaxDrawdownMinor.ShouldBe(30);
    }

    [Fact]
    public void EmptySeries_YieldsEmptyReport()
    {
        var report = RAnalyticsEngine.DrawdownCurve([]);

        report.Series.ShouldBeEmpty();
        report.MaxDrawdownMinor.ShouldBe(0);
        report.CurrentDrawdownMinor.ShouldBe(0);
    }

    [Fact]
    public void NegativeEquity_IsHandled()
    {
        var report = RAnalyticsEngine.DrawdownCurve(Series(50, -20, 10));

        report.MaxDrawdownMinor.ShouldBe(70);
        report.CurrentDrawdownMinor.ShouldBe(40);
    }
}
