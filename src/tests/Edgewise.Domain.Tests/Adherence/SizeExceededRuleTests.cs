using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class SizeExceededRuleTests
{
    // Clean input: allowed risk = 1,000,000 x 0.01 x 1.05 = 10,500 minor.
    // Entry 10,000; planned stop 9,500 -> distance 500 per unit.

    [Fact]
    public void RiskAboveToleratedBudget_DeductsTwentyPoints()
    {
        // 25 x 500 = 12,500 > 10,500.
        var result = AdherenceEngine.Score(TestInputs.Clean() with { ActualMaxPositionQty = 25m });

        var deduction = result.Deductions.ShouldHaveSingleItem();
        deduction.Code.ShouldBe(AdherenceEngine.Codes.SizeExceeded);
        deduction.Points.ShouldBe(20);
        result.Score.ShouldBe(80);
    }

    [Fact]
    public void RiskExactlyAtToleratedBudget_DoesNotDeduct()
    {
        // 21 x 500 = 10,500 == allowed. Comparison is strict.
        var result = AdherenceEngine.Score(TestInputs.Clean() with { ActualMaxPositionQty = 21m });

        result.Deductions.ShouldBeEmpty();
    }

    [Fact]
    public void RiskJustAboveToleratedBudget_Deducts()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { ActualMaxPositionQty = 21.01m });

        TestInputs.Codes(result).ShouldBe([AdherenceEngine.Codes.SizeExceeded]);
    }

    [Fact]
    public void WithinBudget_ButOnlyBecauseOfTolerance_DoesNotDeduct()
    {
        // 20.5 x 500 = 10,250: above the raw budget (10,000) but inside the 5% tolerance.
        var result = AdherenceEngine.Score(TestInputs.Clean() with { ActualMaxPositionQty = 20.5m });

        result.Deductions.ShouldBeEmpty();
    }

    [Fact]
    public void AverageEntryPrice_IsQuantityWeighted()
    {
        // Fills 5 @ 10,000 and 5 @ 11,000 -> avg 10,500; distance to 9,500 = 1,000.
        // 11 x 1,000 = 11,000 > 10,500 -> deducts.
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            EntryFills =
            [
                new TradeFill(5m, 10_000m, TestInputs.Fill),
                new TradeFill(5m, 11_000m, TestInputs.Fill.AddMinutes(1)),
            ],
            ActualMaxPositionQty = 11m,
        });

        TestInputs.Codes(result).ShouldBe([AdherenceEngine.Codes.SizeExceeded]);
    }

    [Fact]
    public void ShortDirection_UsesAbsoluteDistance()
    {
        // Short: entry 10,000, stop above at 10,500 -> distance 500. 25 x 500 = 12,500 > 10,500.
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            Direction = TradeDirection.Short,
            PlannedStopPrice = 10_500m,
            StopVersions = [new StopVersion(10_500m, TestInputs.Fill.AddHours(-2))],
            ActualMaxPositionQty = 25m,
        });

        TestInputs.Codes(result).ShouldBe([AdherenceEngine.Codes.SizeExceeded]);
    }

    [Fact]
    public void NoEntryFills_RuleIsSkipped()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            EntryFills = [],
            ActualMaxPositionQty = 1_000m,
        });

        result.Deductions.ShouldBeEmpty();
    }
}
