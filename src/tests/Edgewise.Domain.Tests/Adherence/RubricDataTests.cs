using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class RubricDataTests
{
    [Fact]
    public void RubricVersion_IsV1()
    {
        AdherenceEngine.RubricVersion.ShouldBe("v1");
    }

    [Fact]
    public void Rubric_ContainsAllNineRulesWithExactPoints()
    {
        var byCode = AdherenceEngine.Rubric.ToDictionary(r => r.Code, r => r.Points);

        byCode.ShouldBe(new Dictionary<string, int>
        {
            ["NO_PLAN"] = 40,
            ["SIZE_EXCEEDED"] = 20,
            ["STOP_WIDENED"] = 25,
            ["NO_TRIGGER"] = 15,
            ["EARLY_EXIT"] = 10,
            ["REVENGE_WINDOW"] = 20,
            ["RED_EVENT"] = 15,
            ["CB_OVERRIDE"] = 25,
            ["HEAT_CAP"] = 15,
        });
    }

    [Fact]
    public void OnlyNoTrigger_IsSelfReport()
    {
        AdherenceEngine.Rubric.Where(r => r.IsSelfReport).Select(r => r.Code)
            .ShouldBe([AdherenceEngine.Codes.NoTrigger]);
    }

    [Fact]
    public void AllRules_HaveDescriptions()
    {
        AdherenceEngine.Rubric.ShouldAllBe(r => !string.IsNullOrWhiteSpace(r.Description));
    }
}
