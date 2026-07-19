using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class StopWidenedRuleTests
{
    [Fact]
    public void LongStopLoweredAfterEntry_DeductsTwentyFivePoints()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            StopVersions =
            [
                new StopVersion(9_500m, TestInputs.Fill.AddHours(-2)),
                new StopVersion(9_400m, TestInputs.Fill.AddMinutes(10)),
            ],
        });

        var deduction = result.Deductions.ShouldHaveSingleItem();
        deduction.Code.ShouldBe(AdherenceEngine.Codes.StopWidened);
        deduction.Points.ShouldBe(25);
        result.Score.ShouldBe(75);
    }

    [Fact]
    public void LongStopRaisedAfterEntry_Tightened_DoesNotDeduct()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            StopVersions =
            [
                new StopVersion(9_500m, TestInputs.Fill.AddHours(-2)),
                new StopVersion(9_700m, TestInputs.Fill.AddMinutes(10)),
            ],
        });

        result.Deductions.ShouldBeEmpty();
    }

    [Fact]
    public void ShortStopRaisedAfterEntry_DeductsTwentyFivePoints()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            Direction = TradeDirection.Short,
            PlannedStopPrice = 10_500m,
            StopVersions =
            [
                new StopVersion(10_500m, TestInputs.Fill.AddHours(-2)),
                new StopVersion(10_700m, TestInputs.Fill.AddMinutes(10)),
            ],
        });

        TestInputs.Codes(result).ShouldBe([AdherenceEngine.Codes.StopWidened]);
    }

    [Fact]
    public void ShortStopLoweredAfterEntry_Tightened_DoesNotDeduct()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            Direction = TradeDirection.Short,
            PlannedStopPrice = 10_500m,
            StopVersions =
            [
                new StopVersion(10_500m, TestInputs.Fill.AddHours(-2)),
                new StopVersion(10_300m, TestInputs.Fill.AddMinutes(10)),
            ],
        });

        result.Deductions.ShouldBeEmpty();
    }

    [Fact]
    public void StopChangedBeforeEntry_DoesNotDeduct()
    {
        // Widening the stop while still planning is allowed; only post-fill widening counts.
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            StopVersions =
            [
                new StopVersion(9_600m, TestInputs.Fill.AddHours(-3)),
                new StopVersion(9_500m, TestInputs.Fill.AddHours(-1)),
            ],
        });

        result.Deductions.ShouldBeEmpty();
    }

    [Fact]
    public void ComparisonUsesVersionActiveAtEntry_NotPlannedStop()
    {
        // Planned stop 9,500 but the version active at entry tightened to 9,800.
        // Moving back to 9,600 after the fill is a widening relative to 9,800.
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            StopVersions =
            [
                new StopVersion(9_500m, TestInputs.Fill.AddHours(-2)),
                new StopVersion(9_800m, TestInputs.Fill.AddHours(-1)),
                new StopVersion(9_600m, TestInputs.Fill.AddMinutes(15)),
            ],
        });

        TestInputs.Codes(result).ShouldBe([AdherenceEngine.Codes.StopWidened]);
    }

    [Fact]
    public void NoVersionBeforeEntry_FallsBackToPlannedStopBaseline()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            StopVersions = [new StopVersion(9_450m, TestInputs.Fill.AddMinutes(5))],
        });

        TestInputs.Codes(result).ShouldBe([AdherenceEngine.Codes.StopWidened]);
    }

    [Fact]
    public void MultipleWidenings_DeductOnlyOnce()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            StopVersions =
            [
                new StopVersion(9_500m, TestInputs.Fill.AddHours(-2)),
                new StopVersion(9_400m, TestInputs.Fill.AddMinutes(10)),
                new StopVersion(9_300m, TestInputs.Fill.AddMinutes(20)),
            ],
        });

        result.Deductions.Count(d => d.Code == AdherenceEngine.Codes.StopWidened).ShouldBe(1);
        result.Score.ShouldBe(75);
    }
}
