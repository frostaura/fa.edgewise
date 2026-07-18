using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class RevengeWindowRuleTests
{
    [Fact]
    public void EntryWithinThirtyMinutesOfLoss_DeductsTwentyPoints()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            MinutesSincePriorLossSameClass = 29,
        });

        var deduction = result.Deductions.ShouldHaveSingleItem();
        deduction.Code.ShouldBe(AdherenceEngine.Codes.RevengeWindow);
        deduction.Points.ShouldBe(20);
        result.Score.ShouldBe(80);
    }

    [Fact]
    public void ExactlyThirtyMinutes_DoesNotDeduct()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            MinutesSincePriorLossSameClass = 30,
        });

        result.Deductions.ShouldBeEmpty();
    }

    [Fact]
    public void ZeroMinutes_Deducts()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            MinutesSincePriorLossSameClass = 0,
        });

        TestInputs.Codes(result).ShouldBe([AdherenceEngine.Codes.RevengeWindow]);
    }

    [Fact]
    public void NoPriorLoss_DoesNotDeduct()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            MinutesSincePriorLossSameClass = null,
        });

        result.Deductions.ShouldBeEmpty();
    }
}
