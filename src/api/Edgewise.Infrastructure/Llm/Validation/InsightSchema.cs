using System.Text.Json;
using Edgewise.Domain.Engines.Coach;
using Json.Schema;

namespace Edgewise.Infrastructure.Llm.Validation;

/// <summary>
/// JSON-Schema (JsonSchema.Net) + pure-domain validation pipeline for LLM-produced insight JSON:
///  (a) parse + schema validate; (b) citation check (uncited items dropped);
///  (c) directive gate; (d) 150-word cap. See <see cref="InsightValidator"/> for (b)-(d).
/// </summary>
public static class InsightSchema
{
    public const string PromptVersion = "v1";

    public const string SchemaJson = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "properties": {
        "observations": { "$ref": "#/$defs/items" },
        "deviations": { "$ref": "#/$defs/items" },
        "riskFlags": { "$ref": "#/$defs/items" },
        "patternLinks": { "$ref": "#/$defs/items" },
        "question": { "$ref": "#/$defs/item" },
        "kudos": { "$ref": "#/$defs/item" }
      },
      "$defs": {
        "item": {
          "type": "object",
          "properties": {
            "text": { "type": "string", "minLength": 1 },
            "citations": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["text", "citations"]
        },
        "items": { "type": "array", "items": { "$ref": "#/$defs/item" } }
      }
    }
    """;

    private static readonly JsonSchema Schema = JsonSchema.FromText(SchemaJson);

    /// <summary>Runs the full pipeline. Errors are phrased so they can be appended to a retry prompt.</summary>
    public static InsightValidationResult Validate(string json, IReadOnlySet<string> validCitationIds)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return InsightValidationResult.Fail($"invalid JSON: {ex.Message}");
        }

        using (document)
        {
            var evaluation = Schema.Evaluate(
                document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!evaluation.IsValid)
            {
                var errors = (evaluation.Details ?? [])
                    .Where(d => d.Errors is { Count: > 0 })
                    .SelectMany(d => d.Errors!.Select(e => $"schema violation at {d.InstanceLocation}: {e.Value}"))
                    .Distinct()
                    .Take(10)
                    .ToArray();
                return InsightValidationResult.Fail(errors.Length > 0 ? errors : ["schema violation"]);
            }
        }

        return InsightValidator.Validate(json, validCitationIds);
    }
}
