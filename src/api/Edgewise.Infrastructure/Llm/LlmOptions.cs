using Microsoft.Extensions.Configuration;

namespace Edgewise.Infrastructure.Llm;

/// <summary>
/// LLM configuration. The API key comes from the ANTHROPIC_API_KEY environment variable
/// (or Llm:ApiKey); model ids from Llm:SmallModel / Llm:LargeModel with sensible defaults.
/// </summary>
public sealed class LlmOptions
{
    public const string DefaultSmallModel = "claude-haiku-4-5-20251001";
    public const string DefaultLargeModel = "claude-sonnet-5";

    public string? ApiKey { get; init; }
    public string SmallModel { get; init; } = DefaultSmallModel;
    public string LargeModel { get; init; } = DefaultLargeModel;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public static LlmOptions From(IConfiguration config) => new()
    {
        ApiKey = config["ANTHROPIC_API_KEY"] ?? config["Llm:ApiKey"],
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
