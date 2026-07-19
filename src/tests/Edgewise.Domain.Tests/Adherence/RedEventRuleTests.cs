using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class RedEventRuleTests
{
    [Fact]
    public void TradedThroughRedEvent_DeductsFifteenPoints()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { TradedThroughRedEvent = true });

        var deduction = result.Deductions.ShouldHaveSingleItem();
        deduction.Code.ShouldBe(AdherenceEngine.Codes.RedEvent);
        deduction.Points.ShouldBe(15);
        result.Score.ShouldBe(85);
    }

    [Fact]
    public void NoRedEvent_DoesNotDeduct()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { TradedThroughRedEvent = false });

        result.Deductions.ShouldBeEmpty();
    }
}
