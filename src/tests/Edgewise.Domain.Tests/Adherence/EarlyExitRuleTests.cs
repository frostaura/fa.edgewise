using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class EarlyExitRuleTests
{
    [Fact]
    public void EarlyDiscretionaryExit_DeductsTenPoints()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            ExitRule = ExitRuleOutcome.EarlyDiscretionary,
        });

        var deduction = result.Deductions.ShouldHaveSingleItem();
        deduction.Code.ShouldBe(AdherenceEngine.Codes.EarlyExit);
        deduction.Points.ShouldBe(10);
        result.Score.ShouldBe(90);
        result.Grade.ShouldBe("A");
    }

    [Theory]
    [InlineData(ExitRuleOutcome.MatchedRule)]
    [InlineData(ExitRuleOutcome.StopHit)]
    [InlineData(ExitRuleOutcome.TimeStop)]
    public void OtherExitOutcomes_DoNotDeduct(ExitRuleOutcome outcome)
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { ExitRule = outcome });

        result.Deductions.ShouldBeEmpty();
        result.Score.ShouldBe(100);
    }
}
