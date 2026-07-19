using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class GradeBoundaryTests
{
    [Theory]
    [InlineData(100, "A")]
    [InlineData(90, "A")]
    [InlineData(89, "B")]
    [InlineData(75, "B")]
    [InlineData(74, "C")]
    [InlineData(60, "C")]
    [InlineData(59, "D")]
    [InlineData(40, "D")]
    [InlineData(39, "F")]
    [InlineData(1, "F")]
    [InlineData(0, "F")]
    public void GradeFor_MapsBoundariesExactly(int score, string expected)
    {
        AdherenceEngine.GradeFor(score).ShouldBe(expected);
    }

    [Fact]
    public void ScoredTrade_GradeMatchesGradeFor()
    {
        // 100 - 10 (EARLY_EXIT) = 90 -> A boundary through the full engine.
        var atNinety = AdherenceEngine.Score(TestInputs.Clean() with
        {
            ExitRule = ExitRuleOutcome.EarlyDiscretionary,
        });
        atNinety.Score.ShouldBe(90);
        atNinety.Grade.ShouldBe("A");

        // 100 - 25 (CB_OVERRIDE) = 75 -> B boundary.
        var atSeventyFive = AdherenceEngine.Score(TestInputs.Clean() with
        {
            CircuitBreakerOverride = true,
        });
        atSeventyFive.Score.ShouldBe(75);
        atSeventyFive.Grade.ShouldBe("B");

        // NO_PLAN alone = 60 -> C boundary.
        var atSixty = AdherenceEngine.Score(TestInputs.Clean() with { PlanCreatedAt = null });
        atSixty.Score.ShouldBe(60);
        atSixty.Grade.ShouldBe("C");

        // 100 - 40 - 20 = 40 -> D boundary (NO_PLAN + REVENGE, capped at 60 first is irrelevant: 40 < 60).
        var atForty = AdherenceEngine.Score(TestInputs.Clean() with
        {
            PlanCreatedAt = null,
            MinutesSincePriorLossSameClass = 5,
        });
        atForty.Score.ShouldBe(40);
        atForty.Grade.ShouldBe("D");
    }
}
