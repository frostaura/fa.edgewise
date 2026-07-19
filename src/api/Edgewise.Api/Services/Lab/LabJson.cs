using System.Text.Json;
using System.Text.Json.Serialization;

namespace Edgewise.Api.Services.Lab;

/// <summary>Serializer options matching the API contract (camelCase, enums as camelCase strings).</summary>
public static class LabJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Parses stored JSON into a detached JsonElement (or null for null/blank input).</summary>
    public static JsonElement? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
