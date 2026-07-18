using System.Text.Json;
using System.Text.Json.Nodes;
using Edgewise.Domain.Engines.Backtesting;
using Json.Schema;

namespace Edgewise.Api.Services.Lab;

/// <summary>
/// Validates strategy rule-tree JSON against a JSON Schema mirroring the
/// Edgewise.Domain.Engines.Backtesting records, and maps valid JSON onto those
/// records. Registered as a singleton (the compiled schema is immutable).
/// </summary>
public sealed class RuleTreeValidator
{
    /// <summary>The canonical rule-tree JSON Schema (draft 2020-12).</summary>
    public const string SchemaText = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["direction", "conditions", "exits", "risk"],
          "additionalProperties": false,
          "properties": {
            "name": { "type": "string", "maxLength": 200 },
            "direction": { "enum": ["long", "short"] },
            "combinator": { "enum": ["and"] },
            "conditions": {
              "type": "array",
              "minItems": 1,
              "maxItems": 6,
              "items": {
                "type": "object",
                "required": ["indicator", "operator", "operand"],
                "additionalProperties": false,
                "properties": {
                  "indicator": {
                    "enum": ["priceVsMa", "rsiBand", "breakoutNBarHigh", "volumeVsAvg", "macdCross", "priceVsBollinger"]
                  },
                  "params": { "type": "object", "additionalProperties": { "type": "number" } },
                  "operator": { "enum": ["gt", "lt", "crossAbove", "crossBelow", "within"] },
                  "operand": { "type": "number" },
                  "operand2": { "type": ["number", "null"] },
                  "enabled": { "type": "boolean" }
                }
              }
            },
            "exits": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "breakevenAtR": { "type": ["number", "null"], "exclusiveMinimum": 0 },
                "partialTakeAtR": { "type": ["number", "null"], "exclusiveMinimum": 0 },
                "partialPct": { "type": ["number", "null"], "minimum": 0, "maximum": 100 },
                "trailMethod": { "enum": ["none", "atrMult", "pctFromPeak"] },
                "trailParam": { "type": ["number", "null"], "minimum": 0 },
                "timeStopBars": { "type": ["integer", "null"], "minimum": 1 },
                "stopAtrMult": { "type": ["number", "null"], "exclusiveMinimum": 0 },
                "stopPct": { "type": ["number", "null"], "exclusiveMinimum": 0 }
              }
            },
            "risk": {
              "type": "object",
              "required": ["riskPctPerTrade"],
              "additionalProperties": false,
              "properties": {
                "riskPctPerTrade": { "type": "number", "exclusiveMinimum": 0, "maximum": 100 }
              }
            }
          }
        }
        """;

    private readonly JsonSchema _schema = JsonSchema.FromText(SchemaText);

    /// <summary>
    /// Validates rule-tree JSON. Returns an empty dictionary when valid; otherwise a map of
    /// field path (e.g. <c>conditions[1].operand2</c>) to a human-readable error.
    /// </summary>
    public IReadOnlyDictionary<string, string> Validate(JsonElement ruleTree)
    {
        var fields = new Dictionary<string, string>();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(ruleTree.GetRawText());
        }
        catch (JsonException)
        {
            fields["$"] = "ruleTree must be a JSON object.";
            return fields;
        }

        var results = _schema.Evaluate(ruleTree, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!results.IsValid)
        {
            foreach (var detail in results.Details)
            {
                if (detail.Errors is null || detail.Errors.Count == 0)
                {
                    continue;
                }

                var key = PointerToField(detail.InstanceLocation.ToString());
                var message = string.Join("; ", detail.Errors.Values);
                fields[key] = fields.TryGetValue(key, out var existing) ? $"{existing}; {message}" : message;
            }

            if (fields.Count == 0)
            {
                fields["$"] = "ruleTree failed schema validation.";
            }

            return fields;
        }

        // Semantic checks the schema cannot express.
        var root = node!.AsObject();

        if (root["conditions"] is JsonArray conditions)
        {
            var anyEnabled = false;
            for (var i = 0; i < conditions.Count; i++)
            {
                var cond = conditions[i]!.AsObject();
                var enabled = cond["enabled"]?.GetValue<bool>() ?? true;
                anyEnabled |= enabled;

                var op = cond["operator"]?.GetValue<string>();
                if (op == "within")
                {
                    var operand2 = cond["operand2"];
                    if (operand2 is null)
                    {
                        fields[$"conditions[{i}].operand2"] = "operand2 is required when operator is 'within'.";
                    }
                    else if (cond["operand"] is JsonNode operand
                        && operand2.GetValue<decimal>() <= operand.GetValue<decimal>())
                    {
                        fields[$"conditions[{i}].operand2"] = "operand2 must be greater than operand for 'within'.";
                    }
                }
            }

            if (!anyEnabled)
            {
                fields["conditions"] = "At least one condition must be enabled.";
            }
        }

        if (root["exits"] is JsonObject exits)
        {
            var partialAt = exits["partialTakeAtR"];
            var partialPct = exits["partialPct"]?.GetValue<decimal>() ?? 0m;
            if (partialAt is not null && partialPct <= 0m)
            {
                fields["exits.partialPct"] = "partialPct must be greater than 0 when partialTakeAtR is set.";
            }

            var trailMethod = exits["trailMethod"]?.GetValue<string>() ?? "none";
            var trailParam = exits["trailParam"]?.GetValue<decimal>() ?? 0m;
            if (trailMethod != "none" && trailParam <= 0m)
            {
                fields["exits.trailParam"] = "trailParam must be greater than 0 when a trail method is selected.";
            }

            if (exits["stopAtrMult"] is not null && exits["stopPct"] is not null)
            {
                fields["exits.stopPct"] = "Set either stopAtrMult or stopPct, not both.";
            }
        }

        return fields;
    }

    /// <summary>Maps validated rule-tree JSON onto the domain engine records.</summary>
    public static (RuleTree Tree, ExitLadder Ladder, decimal RiskPctPerTrade) Map(JsonElement ruleTree)
    {
        var conditions = new List<Condition>();
        foreach (var cond in ruleTree.GetProperty("conditions").EnumerateArray())
        {
            var parameters = new Dictionary<string, decimal>();
            if (cond.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in p.EnumerateObject())
                {
                    parameters[kv.Name] = kv.Value.GetDecimal();
                }
            }

            conditions.Add(new Condition(
                Enum.Parse<IndicatorKind>(cond.GetProperty("indicator").GetString()!, ignoreCase: true),
                parameters,
                Enum.Parse<ConditionOperator>(cond.GetProperty("operator").GetString()!, ignoreCase: true),
                cond.GetProperty("operand").GetDecimal(),
                OptionalDecimal(cond, "operand2"),
                !cond.TryGetProperty("enabled", out var e) || e.GetBoolean()));
        }

        var direction = Enum.Parse<TradeDirection>(ruleTree.GetProperty("direction").GetString()!, ignoreCase: true);
        var tree = new RuleTree(conditions, RuleCombinator.And, direction);

        var exits = ruleTree.GetProperty("exits");
        var ladder = new ExitLadder(
            BreakevenAtR: OptionalDecimal(exits, "breakevenAtR"),
            PartialTakeAtR: OptionalDecimal(exits, "partialTakeAtR"),
            PartialPct: OptionalDecimal(exits, "partialPct") ?? 0m,
            TrailMethod: exits.TryGetProperty("trailMethod", out var tm) && tm.ValueKind == JsonValueKind.String
                ? Enum.Parse<TrailMethod>(tm.GetString()!, ignoreCase: true)
                : TrailMethod.None,
            TrailParam: OptionalDecimal(exits, "trailParam") ?? 0m,
            TimeStopBars: OptionalDecimal(exits, "timeStopBars") is decimal tsb ? (int)tsb : null,
            StopAtrMult: OptionalDecimal(exits, "stopAtrMult"),
            StopPct: OptionalDecimal(exits, "stopPct"));

        var riskPct = ruleTree.GetProperty("risk").GetProperty("riskPctPerTrade").GetDecimal();

        return (tree, ladder, riskPct);
    }

    private static decimal? OptionalDecimal(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDecimal()
            : null;

    /// <summary>Converts a JSON pointer ("/conditions/0/operand") into a field path ("conditions[0].operand").</summary>
    private static string PointerToField(string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
        {
            return "$";
        }

        var segments = pointer.TrimStart('/').Split('/');
        var parts = new List<string>();
        foreach (var segment in segments)
        {
            if (int.TryParse(segment, out var index) && parts.Count > 0)
            {
                parts[^1] += $"[{index}]";
            }
            else
            {
                parts.Add(segment.Replace("~1", "/").Replace("~0", "~"));
            }
        }

        return string.Join('.', parts);
    }
}
