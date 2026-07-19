using Edgewise.Domain.Engines.Analytics;
using Shouldly;
using static Edgewise.Domain.Tests.Analytics.AnalyticsTestData;

namespace Edgewise.Domain.Tests.Analytics;

public class TrendAndCalendarTests
{
    // 2026-06-01 is a Monday.
    private static readonly DateTime Monday = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PtrTrend_ComputesPlannedRatePerWeek()
    {
        var trades = new[]
        {
            Trade(Monday, hadPlan: true),
            Trade(Monday.AddDays(2), hadPlan: false),
            Trade(Monday.AddDays(4), hadPlan: true),
            Trade(Monday.AddDays(6), hadPlan: true),  // Sunday, still week 1
            Trade(Monday.AddDays(7), hadPlan: false), // next Monday, week 2
        };

        var trend = RAnalyticsEngine.PtrTrend(trades);

        trend.Count.ShouldBe(2);
        trend[0].WeekStart.ShouldBe(new DateOnly(2026, 6, 1));
        trend[0].N.ShouldBe(4);
        trend[0].PlannedN.ShouldBe(3);
        trend[0].PlannedRate.ShouldBe(0.75m);
        trend[1].WeekStart.ShouldBe(new DateOnly(2026, 6, 8));
        trend[1].PlannedRate.ShouldBe(0m);
    }

    [Fact]
    public void WeekStartOf_AlwaysReturnsMonday()
    {
        RAnalyticsEngine.WeekStartOf(new DateOnly(2026, 6, 1)).ShouldBe(new DateOnly(2026, 6, 1)); // Monday
        RAnalyticsEngine.WeekStartOf(new DateOnly(2026, 6, 3)).ShouldBe(new DateOnly(2026, 6, 1)); // Wednesday
        RAnalyticsEngine.WeekStartOf(new DateOnly(2026, 6, 7)).ShouldBe(new DateOnly(2026, 6, 1)); // Sunday
        RAnalyticsEngine.WeekStartOf(new DateOnly(2026, 6, 8)).ShouldBe(new DateOnly(2026, 6, 8)); // next Monday
    }

    [Fact]
    public void AdherenceTrend_AveragesOnlyScoredTrades()
    {
        var trades = new[]
        {
            Trade(Monday, adherence: 80),
            Trade(Monday.AddDays(1), adherence: 100),
            Trade(Monday.AddDays(2), adherence: null), // ignored
            Trade(Monday.AddDays(8), adherence: 60),
        };

        var trend = RAnalyticsEngine.AdherenceTrend(trades);

        trend.Count.ShouldBe(2);
        trend[0].N.ShouldBe(2);
        trend[0].AvgAdherence.ShouldBe(90m);
        trend[1].N.ShouldBe(1);
        trend[1].AvgAdherence.ShouldBe(60m);
    }

    [Fact]
    public void CalendarHeatmap_AggregatesPerDate()
    {
        var trades = new[]
        {
            Trade(Monday, r: 1.5m, adherence: 90),
            Trade(Monday.AddHours(3), r: -1m, adherence: 70),
            Trade(Monday.AddDays(1), r: 2m, adherence: null),
        };

        var cells = RAnalyticsEngine.CalendarHeatmap(trades);

        cells.Count.ShouldBe(2);
        cells[0].Date.ShouldBe(new DateOnly(2026, 6, 1));
        cells[0].N.ShouldBe(2);
        cells[0].SumR.ShouldBe(0.5m);
        cells[0].AvgAdherence.ShouldBe(80m);
        cells[1].N.ShouldBe(1);
        cells[1].SumR.ShouldBe(2m);
        cells[1].AvgAdherence.ShouldBeNull();
    }

    [Fact]
    public void SessionClock_AggregatesByHourOfDay()
    {
        var trades = new[]
        {
            Trade(Monday, r: 1m),                 // hour 10
            Trade(Monday.AddMinutes(30), r: -2m), // hour 10
            Trade(Monday.AddHours(5), r: 3m),     // hour 15
        };

        var clock = RAnalyticsEngine.SessionClock(trades);

        clock.Count.ShouldBe(2);
        clock[0].HourOfDay.ShouldBe(10);
        clock[0].N.ShouldBe(2);
        clock[0].SumR.ShouldBe(-1m);
        clock[1].HourOfDay.ShouldBe(15);
        clock[1].SumR.ShouldBe(3m);
    }

    [Fact]
    public void EmptyInputs_YieldEmptyResults()
    {
        RAnalyticsEngine.PtrTrend([]).ShouldBeEmpty();
        RAnalyticsEngine.AdherenceTrend([]).ShouldBeEmpty();
        RAnalyticsEngine.CalendarHeatmap([]).ShouldBeEmpty();
        RAnalyticsEngine.SessionClock([]).ShouldBeEmpty();
    }
}
