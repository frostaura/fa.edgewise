using System.Text.Json;

namespace Edgewise.Api.Services.Lab;

public sealed record StrategyTemplateDto(string Id, string Name, string Description, JsonElement RuleTree);

/// <summary>The four playbook-aligned example rule-trees the UI can load as starting points.</summary>
public static class StrategyTemplates
{
    private static readonly (string Id, string Name, string Description, string RuleTree)[] Raw =
    [
        (
            "trend-pullback",
            "Trend pullback",
            "Buy pullbacks in an uptrend: price above the 50-bar MA while RSI cools into the 40-60 band.",
            """
            {
              "name": "Trend pullback",
              "direction": "long",
              "combinator": "and",
              "conditions": [
                { "indicator": "priceVsMa", "params": { "period": 50 }, "operator": "gt", "operand": 0, "enabled": true },
                { "indicator": "rsiBand", "params": { "period": 14 }, "operator": "within", "operand": 40, "operand2": 60, "enabled": true }
              ],
              "exits": {
                "breakevenAtR": 1,
                "partialTakeAtR": 1.5,
                "partialPct": 50,
                "trailMethod": "atrMult",
                "trailParam": 2.5,
                "timeStopBars": 40,
                "stopAtrMult": 2
              },
              "risk": { "riskPctPerTrade": 0.5 }
            }
            """
        ),
        (
            "breakout-retest",
            "Breakout + volume",
            "Enter on a close above the 20-bar high confirmed by volume at least 1.5x its 20-bar average.",
            """
            {
              "name": "Breakout + volume",
              "direction": "long",
              "combinator": "and",
              "conditions": [
                { "indicator": "breakoutNBarHigh", "params": { "n": 20 }, "operator": "gt", "operand": 0, "enabled": true },
                { "indicator": "volumeVsAvg", "params": { "period": 20 }, "operator": "gt", "operand": 1.5, "enabled": true }
              ],
              "exits": {
                "breakevenAtR": 1,
                "partialTakeAtR": 2,
                "partialPct": 50,
                "trailMethod": "pctFromPeak",
                "trailParam": 8,
                "stopAtrMult": 1.5
              },
              "risk": { "riskPctPerTrade": 0.5 }
            }
            """
        ),
        (
            "mean-reversion",
            "Mean reversion",
            "Fade washouts: RSI under 30 with price stretched below the lower Bollinger band; quick time stop.",
            """
            {
              "name": "Mean reversion",
              "direction": "long",
              "combinator": "and",
              "conditions": [
                { "indicator": "rsiBand", "params": { "period": 14 }, "operator": "lt", "operand": 30, "enabled": true },
                { "indicator": "priceVsBollinger", "params": { "period": 20, "sd": 2 }, "operator": "lt", "operand": -1, "enabled": true }
              ],
              "exits": {
                "trailMethod": "none",
                "timeStopBars": 10,
                "stopAtrMult": 2
              },
              "risk": { "riskPctPerTrade": 0.5 }
            }
            """
        ),
        (
            "momentum",
            "Momentum",
            "Ride momentum: MACD crossing above its signal line with price already above the 20-bar MA.",
            """
            {
              "name": "Momentum",
              "direction": "long",
              "combinator": "and",
              "conditions": [
                { "indicator": "macdCross", "params": { "fast": 12, "slow": 26, "signal": 9 }, "operator": "crossAbove", "operand": 0, "enabled": true },
                { "indicator": "priceVsMa", "params": { "period": 20 }, "operator": "gt", "operand": 0, "enabled": true }
              ],
              "exits": {
                "breakevenAtR": 1,
                "trailMethod": "atrMult",
                "trailParam": 3,
                "stopAtrMult": 2
              },
              "risk": { "riskPctPerTrade": 0.5 }
            }
            """
        ),
    ];

    public static readonly IReadOnlyList<StrategyTemplateDto> All = Raw
        .Select(t => new StrategyTemplateDto(t.Id, t.Name, t.Description, LabJson.ParseOrNull(t.RuleTree)!.Value))
        .ToList();
}
