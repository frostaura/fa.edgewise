using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class CbOverrideRuleTests
{
    [Fact]
    public void CircuitBreakerOverride_DeductsTwentyFivePoints()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { CircuitBreakerOverride = true });

        var deduction = result.Deductions.ShouldHaveSingleItem();
        deduction.Code.ShouldBe(AdherenceEngine.Codes.CbOverride);
        deduction.Points.ShouldBe(25);
        result.Score.ShouldBe(75);
        result.Grade.ShouldBe("B");
    }

    [Fact]
    public void NoOverride_DoesNotDeduct()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { CircuitBreakerOverride = false });

        result.Deductions.ShouldBeEmpty();
    }
}
