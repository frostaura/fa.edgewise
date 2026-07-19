using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class HeatCapRuleTests
{
    [Fact]
    public void OpenRiskAboveHeatCap_DeductsFifteenPoints()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            HeatCapPct = 0.04m,
            OpenRiskAtEntryPct = 0.05m,
        });

        var deduction = result.Deductions.ShouldHaveSingleItem();
        deduction.Code.ShouldBe(AdherenceEngine.Codes.HeatCap);
        deduction.Points.ShouldBe(15);
        result.Score.ShouldBe(85);
    }

    [Fact]
    public void OpenRiskExactlyAtHeatCap_DoesNotDeduct()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            HeatCapPct = 0.04m,
            OpenRiskAtEntryPct = 0.04m,
        });

        result.Deductions.ShouldBeEmpty();
    }

    [Fact]
    public void OpenRiskBelowHeatCap_DoesNotDeduct()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            HeatCapPct = 0.04m,
            OpenRiskAtEntryPct = 0.01m,
        });

        result.Deductions.ShouldBeEmpty();
    }
}
