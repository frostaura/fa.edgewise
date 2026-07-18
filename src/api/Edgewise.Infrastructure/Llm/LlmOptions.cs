using Microsoft.Extensions.Configuration;

namespace Edgewise.Infrastructure.Llm;

/// <summary>
/// LLM configuration. OpenRouter is the provider of choice (OPENROUTER_API_KEY); the direct
/// Anthropic API is a secondary fallback (ANTHROPIC_API_KEY). Model ids come from
/// Llm:SmallModel / Llm:LargeModel and default to OpenRouter slugs.
/// </summary>
public sealed class LlmOptions
{
    public const string DefaultSmallModel = "anthropic/claude-haiku-4.5";
    public const string DefaultLargeModel = "anthropic/claude-sonnet-4.5";

    public string? OpenRouterApiKey { get; init; }
    public string? AnthropicApiKey { get; init; }
    public string SmallModel { get; init; } = DefaultSmallModel;
    public string LargeModel { get; init; } = DefaultLargeModel;

    public bool HasOpenRouterKey => !string.IsNullOrWhiteSpace(OpenRouterApiKey);
    public bool HasAnthropicKey => !string.IsNullOrWhiteSpace(AnthropicApiKey);

    public static LlmOptions From(IConfiguration config) => new()
    {
        OpenRouterApiKey = config["OPENROUTER_API_KEY"] ?? config["Llm:OpenRouterApiKey"],
        AnthropicApiKey = config["ANTHROPIC_API_KEY"] ?? config["Llm:ApiKey"],
        SmallModel = config["Llm:SmallModel"] ?? DefaultSmallModel,
        LargeModel = config["Llm:LargeModel"] ?? DefaultLargeModel,
    };
}

/// <summary>
/// Cost estimation table, in micro-USD per token. Small tier: $1 / $5 per MTok in/out;
/// large tier: $3 / $15 per MTok in/out ($1 per MTok == 1 micro-USD per token).
/// </summary>
public static class LlmCost
{
    public static long EstimateMicroUsd(LlmOptions options, string model, int tokensIn, int tokensOut)
    {
        var isSmall = string.Equals(model, options.SmallModel, StringComparison.OrdinalIgnoreCase)
            || model.Contains("haiku", StringComparison.OrdinalIgnoreCase);
        var (inRate, outRate) = isSmall ? (1L, 5L) : (3L, 15L);
        return tokensIn * inRate + tokensOut * outRate;
    }
}
