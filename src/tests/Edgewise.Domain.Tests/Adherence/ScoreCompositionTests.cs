using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

public class ScoreCompositionTests
{
    [Fact]
    public void CleanTrade_ScoresOneHundred_GradeA()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean());

        result.Score.ShouldBe(100);
        result.Grade.ShouldBe("A");
        result.Deductions.ShouldBeEmpty();
        result.RubricVersion.ShouldBe("v1");
    }

    [Fact]
    public void UnplannedTrade_IsCappedAtSixty()
    {
        var result = AdherenceEngine.Score(TestInputs.Clean() with { PlanCreatedAt = null });

        result.Score.ShouldBe(60);
        result.Score.ShouldBeLessThanOrEqualTo(AdherenceEngine.UnplannedScoreCap);
    }

    [Fact]
    public void UnplannedTrade_WithFurtherDeductions_DropsBelowCap()
    {
        // NO_PLAN (40) + NO_TRIGGER (15) = 55 -> 45.
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            PlanCreatedAt = null,
            TriggerChecklistConfirmed = false,
        });

        result.Score.ShouldBe(45);
        result.Grade.ShouldBe("D");
    }

    [Fact]
    public void StackedDeductions_SumIndividually()
    {
        // SIZE_EXCEEDED (20) + REVENGE_WINDOW (20) + RED_EVENT (15) = 55 -> 45.
        var result = AdherenceEngine.Score(TestInputs.Clean() with
        {
            ActualMaxPositionQty = 25m,
            MinutesSincePriorLossSameClass = 10,
            TradedThroughRedEvent = true,
        });

        TestInputs.Codes(result).ShouldBe(
            [
                AdherenceEngine.Codes.SizeExceeded,
                AdherenceEngine.Codes.RevengeWindow,
                AdherenceEngine.Codes.RedEvent,
            ],
            ignoreOrder: true);
        result.Score.ShouldBe(45);
        result.Grade.ShouldBe("D");
    }

    [Fact]
    public void AllRulesBreached_FloorsAtZero()
    {
        var result = AdherenceEngine.Score(WorstTrade());

        result.Deductions.Count.ShouldBe(AdherenceEngine.Rubric.Count);
        result.Deductions.Sum(d => d.Points).ShouldBe(185);
        result.Score.ShouldBe(0);
        result.Grade.ShouldBe("F");
    }

    [Fact]
    public void DeductionPoints_AlwaysMatchRubric()
    {
        var result = AdherenceEngine.Score(WorstTrade());

        foreach (var deduction in result.Deductions)
        {
            var rule = AdherenceEngine.Rubric.Single(r => r.Code == deduction.Code);
            deduction.Points.ShouldBe(rule.Points);
        }
    }

    [Fact]
    public void Score_IsAlwaysWithinBounds_AndUnplannedNeverExceedsCap()
    {
        // Deterministic sweep over all combinations of the boolean-ish rule toggles.
        for (var mask = 0; mask < (1 << 9); mask++)
        {
            var input = TestInputs.Clean() with
            {
                PlanCreatedAt = (mask & 1) != 0 ? null : TestInputs.Fill.AddHours(-2),
                ActualMaxPositionQty = (mask & 2) != 0 ? 25m : 10m,
                StopVersions = (mask & 4) != 0
                    ?
                    [
                        new StopVersion(9_500m, TestInputs.Fill.AddHours(-2)),
                        new StopVersion(9_300m, TestInputs.Fill.AddMinutes(5)),
                    ]
                    : [new StopVersion(9_500m, TestInputs.Fill.AddHours(-2))],
                TriggerChecklistConfirmed = (mask & 8) == 0,
                ExitRule = (mask & 16) != 0 ? ExitRuleOutcome.EarlyDiscretionary : ExitRuleOutcome.MatchedRule,
                MinutesSincePriorLossSameClass = (mask & 32) != 0 ? 5 : null,
                TradedThroughRedEvent = (mask & 64) != 0,
                CircuitBreakerOverride = (mask & 128) != 0,
                OpenRiskAtEntryPct = (mask & 256) != 0 ? 0.99m : 0.02m,
            };

            var result = AdherenceEngine.Score(input);

            result.Score.ShouldBeInRange(0, 100);
            result.Score.ShouldBe(Math.Max(0, Math.Min(
                (mask & 1) != 0 ? AdherenceEngine.UnplannedScoreCap : 100,
                100 - result.Deductions.Sum(d => d.Points))));
            result.Grade.ShouldBe(AdherenceEngine.GradeFor(result.Score));
        }
    }

    private static AdherenceInput WorstTrade() => TestInputs.Clean() with
    {
        PlanCreatedAt = null,
        ActualMaxPositionQty = 25m,
        StopVersions =
        [
            new StopVersion(9_500m, TestInputs.Fill.AddHours(-2)),
            new StopVersion(9_200m, TestInputs.Fill.AddMinutes(5)),
        ],
        TriggerChecklistConfirmed = false,
        ExitRule = ExitRuleOutcome.EarlyDiscretionary,
        MinutesSincePriorLossSameClass = 3,
        TradedThroughRedEvent = true,
        CircuitBreakerOverride = true,
        OpenRiskAtEntryPct = 0.10m,
    };
}
