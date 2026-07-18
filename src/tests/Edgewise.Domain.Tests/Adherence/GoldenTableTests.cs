using Edgewise.Domain.Engines.Adherence;
using Shouldly;

namespace Edgewise.Domain.Tests.Adherence;

/// <summary>
/// Composite scenarios locking the rubric down as a golden table.
/// Flags: noPlan, oversize, widened, noTrigger, earlyExit, revenge, redEvent, cbOverride, overHeat.
/// </summary>
public class GoldenTableTests
{
    public static IEnumerable<object[]> Scenarios()
    {
        //                    name                          noPlan oversize widened noTrig early  revenge red    cb     heat   score grade
        yield return Case("clean trade",                    false, false, false, false, false, false, false, false, false, 100, "A");
        yield return Case("unplanned only",                 true,  false, false, false, false, false, false, false, false, 60,  "C");
        yield return Case("oversized only",                 false, true,  false, false, false, false, false, false, false, 80,  "B");
        yield return Case("stop widened only",              false, false, true,  false, false, false, false, false, false, 75,  "B");
        yield return Case("no trigger only",                false, false, false, true,  false, false, false, false, false, 85,  "B");
        yield return Case("early exit only",                false, false, false, false, true,  false, false, false, false, 90,  "A");
        yield return Case("revenge only",                   false, false, false, false, false, true,  false, false, false, 80,  "B");
        yield return Case("red event only",                 false, false, false, false, false, false, true,  false, false, 85,  "B");
        yield return Case("cb override only",               false, false, false, false, false, false, false, true,  false, 75,  "B");
        yield return Case("over heat cap only",             false, false, false, false, false, false, false, false, true,  85,  "B");
        yield return Case("sloppy but planned",             false, false, false, true,  true,  false, false, false, false, 75,  "B");
        yield return Case("unplanned and unconfirmed",      true,  false, false, true,  false, false, false, false, false, 45,  "D");
        yield return Case("oversized widened revenge",      false, true,  true,  false, false, true,  false, false, false, 35,  "F");
        yield return Case("unplanned cb revenge",           true,  false, false, false, false, true,  false, true,  false, 15,  "F");
        yield return Case("early exit through red event",   false, false, false, false, true,  false, true,  false, false, 75,  "B");
        yield return Case("hot oversized no-trigger",       false, true,  false, true,  false, false, false, false, true,  50,  "D");
        yield return Case("everything wrong",               true,  true,  true,  true,  true,  true,  true,  true,  true,  0,   "F");

        static object[] Case(
            string name, bool noPlan, bool oversize, bool widened, bool noTrigger, bool earlyExit,
            bool revenge, bool redEvent, bool cbOverride, bool overHeat, int score, string grade) =>
            [name, noPlan, oversize, widened, noTrigger, earlyExit, revenge, redEvent, cbOverride, overHeat, score, grade];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void CompositeScenario_ProducesExpectedScoreAndGrade(
        string name,
        bool noPlan,
        bool oversize,
        bool widened,
        bool noTrigger,
        bool earlyExit,
        bool revenge,
        bool redEvent,
        bool cbOverride,
        bool overHeat,
        int expectedScore,
        string expectedGrade)
    {
        var input = TestInputs.Clean() with
        {
            PlanCreatedAt = noPlan ? null : TestInputs.Fill.AddHours(-2),
            ActualMaxPositionQty = oversize ? 25m : 10m,
            StopVersions = widened
                ?
                [
                    new StopVersion(9_500m, TestInputs.Fill.AddHours(-2)),
                    new StopVersion(9_350m, TestInputs.Fill.AddMinutes(10)),
                ]
                : [new StopVersion(9_500m, TestInputs.Fill.AddHours(-2))],
            TriggerChecklistConfirmed = !noTrigger,
            ExitRule = earlyExit ? ExitRuleOutcome.EarlyDiscretionary : ExitRuleOutcome.MatchedRule,
            MinutesSincePriorLossSameClass = revenge ? 12 : null,
            TradedThroughRedEvent = redEvent,
            CircuitBreakerOverride = cbOverride,
            OpenRiskAtEntryPct = overHeat ? 0.08m : 0.02m,
        };

        var result = AdherenceEngine.Score(input);

        result.Score.ShouldBe(expectedScore, $"scenario: {name}");
        result.Grade.ShouldBe(expectedGrade, $"scenario: {name}");
    }
}
