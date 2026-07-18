using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Edgewise.Domain.Engines.Coach;
using Edgewise.Domain.Entities;

namespace Edgewise.Api.Services.Coach;

/// <summary>
/// The grounding context handed to the model: a list of id-tagged facts. The ids are the ONLY
/// citations the validator will accept, which is what makes insights grounded-by-construction.
/// </summary>
public sealed class ContextPack
{
    private readonly List<(string Id, string Fact)> _facts = [];
    private readonly Dictionary<string, string> _labels = [];

    public IReadOnlySet<string> ValidIds => _labels.Keys.ToHashSet();

    public void Add(string id, string label, string fact)
    {
        _facts.Add((id, fact));
        _labels.TryAdd(id, label);
    }

    public string LabelFor(string reference) =>
        _labels.TryGetValue(reference, out var label) ? label : reference;

    /// <summary>Renders "[id] fact" lines for the prompt.</summary>
    public string RenderFacts()
    {
        var sb = new StringBuilder();
        foreach (var (id, fact) in _facts)
        {
            sb.Append('[').Append(id).Append("] ").AppendLine(fact);
        }

        return sb.ToString();
    }

    /// <summary>Resolves the citations used by <paramref name="content"/> into labelled refs for CitationsJson.</summary>
    public List<CitationDto> ResolveCitations(InsightContent content) =>
        [.. content.AllItems()
            .SelectMany(i => i.Citations)
            .Distinct()
            .Select(r => new CitationDto(r, LabelFor(r)))];
}

/// <summary>Serialisation helpers shared by the coach vertical.</summary>
public static class CoachJson
{
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialises insight content (plus optional extra metadata fields) for Insight.ContentJson.</summary>
    public static string SerializeContent(InsightContent content, IDictionary<string, string?>? meta = null)
    {
        var node = JsonSerializer.SerializeToNode(content, CamelCase)!.AsObject();
        node.Remove("isEmpty");
        if (meta is not null)
        {
            foreach (var (key, value) in meta)
            {
                if (value is not null)
                {
                    node[key] = value;
                }
            }
        }

        return node.ToJsonString();
    }

    public static string SerializeCitations(IReadOnlyList<CitationDto> citations) =>
        JsonSerializer.Serialize(citations, CamelCase);

    public static InsightDto ToDto(Insight insight)
    {
        JsonNode? content = null;
        try
        {
            content = JsonNode.Parse(insight.ContentJson);
        }
        catch (JsonException)
        {
            // Leave content null on corrupt rows rather than failing the request.
        }

        List<CitationDto> citations = [];
        if (!string.IsNullOrWhiteSpace(insight.CitationsJson))
        {
            try
            {
                citations = JsonSerializer.Deserialize<List<CitationDto>>(insight.CitationsJson, CamelCase) ?? [];
            }
            catch (JsonException)
            {
            }
        }

        return new InsightDto(
            insight.Id, insight.Type, insight.TradeId, content, citations,
            insight.ModelTag, insight.PromptVersion, insight.Feedback, insight.FeedbackReason,
            insight.CreatedAt);
    }
}
