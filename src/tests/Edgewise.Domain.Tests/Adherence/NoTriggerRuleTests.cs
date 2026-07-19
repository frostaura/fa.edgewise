using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class NoTriggerRuleTests
{
    [Fact]
    public void ChecklistNotConfirmed_DeductsFifteenPoints()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { TriggerChecklistConfirmed = false });

        var deduction = result.Deductions.ShouldHaveSingleItem();
        deduction.Code.ShouldBe(AdherenceEngine.Codes.NoTrigger);
        deduction.Points.ShouldBe(15);
        result.Score.ShouldBe(85);
    }

    [Fact]
    public void Evidence_IsMarkedSelfReport()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { TriggerChecklistConfirmed = false });

        result.Deductions[0].Evidence.ShouldContain("self-report");
    }

    [Fact]
    public void ChecklistConfirmed_DoesNotDeduct()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { TriggerChecklistConfirmed = true });

        result.Deductions.ShouldBeEmpty();
    }

    [Fact]
    public void RubricMarksNoTriggerAsSelfReport()
    {
        AdherenceEngine.Rubric.Single(r => r.Code == AdherenceEngine.Codes.NoTrigger)
            .IsSelfReport.ShouldBeTrue();
    }
}
