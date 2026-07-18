using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class NoPlanRuleTests
{
    [Fact]
    public void NullPlanCreatedAt_DeductsFortyPoints()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { PlanCreatedAt = null });

        var deduction = result.Deductions.ShouldHaveSingleItem();
        deduction.Code.ShouldBe(AdherenceEngine.Codes.NoPlan);
        deduction.Points.ShouldBe(40);
        result.Score.ShouldBe(60);
    }

    [Fact]
    public void PlanCreatedAfterFirstFill_DeductsFortyPoints()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            PlanCreatedAt = TestInputs.Fill.AddMinutes(1),
        });

        TestInputs.Codes(result).ShouldBe([AdherenceEngine.Codes.NoPlan]);
        result.Score.ShouldBe(60);
    }

    [Fact]
    public void PlanCreatedExactlyAtFirstFill_CountsAsUnplanned()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            PlanCreatedAt = TestInputs.Fill,
        });

        TestInputs.Codes(result).ShouldBe([AdherenceEngine.Codes.NoPlan]);
    }

    [Fact]
    public void PlanCreatedOneTickBeforeFirstFill_IsPlanned()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            PlanCreatedAt = TestInputs.Fill.AddTicks(-1),
        });

        result.Deductions.ShouldBeEmpty();
        result.Score.ShouldBe(100);
    }

    [Fact]
    public void UnplannedEvidence_ExplainsWhy()
    {
        var nullPlan = AdherenceEngine.Score(TestInputs.Clean() with { PlanCreatedAt = null });
        nullPlan.Deductions[0].Evidence.ShouldContain("No plan");

        var latePlan = AdherenceEngine.Score(TestInputs.Clean() with
        {
            PlanCreatedAt = TestInputs.Fill.AddMinutes(5),
        });
        latePlan.Deductions[0].Evidence.ShouldContain("not before first fill");
    }
}
