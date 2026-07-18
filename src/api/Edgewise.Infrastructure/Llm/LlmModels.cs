using System.Text.Json.Nodes;

namespace Edgewise.Infrastructure.Llm;

/// <summary>
/// One provider-agnostic conversation message. Plain turns set <see cref="Text"/>; the chat
/// tool-use loop additionally sets <see cref="ToolCalls"/> (assistant turns that requested
/// tools) and <see cref="ToolResults"/> (the corresponding results). Each provider serialises
/// these to its own wire format (OpenAI-style tool_calls/tool messages for OpenRouter,
/// content blocks for Anthropic).
/// </summary>
public sealed class LlmMessage
{
    public required string Role { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }
    public IReadOnlyList<LlmToolResult>? ToolResults { get; init; }

    public static LlmMessage User(string text) => new() { Role = "user", Text = text };
    public static LlmMessage Assistant(string text) => new() { Role = "assistant", Text = text };
}

/// <summary>A tool exposed to the model.</summary>
public sealed class LlmToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON Schema for the tool input.</summary>
    public required JsonNode InputSchema { get; init; }
}

public sealed class LlmRequest
{
    public string? System { get; init; }
    public required IReadOnlyList<LlmMessage> Messages { get; init; }
    public int MaxTokens { get; init; } = 1024;
    public required string Model { get; init; }
    /// <summary>Hint that the reply must be a single JSON document (reinforced via the system prompt).</summary>
    public bool JsonMode { get; init; }
    public IReadOnlyList<LlmToolDefinition>? Tools { get; init; }
}

public sealed class LlmToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>The tool input/arguments as serialised JSON.</summary>
    public required string InputJson { get; init; }
}

public sealed record LlmToolResult(string CallId, string Content);

public sealed class LlmResult
{
    /// <summary>True when no provider is configured (NullLlmProvider) — callers must fall back to deterministic output.</summary>
    public bool IsOffline { get; init; }
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];
    /// <summary>Normalised: "tool_use" when the model requested tools, otherwise the provider's stop/finish reason.</summary>
    public string StopReason { get; init; } = string.Empty;
    public int TokensIn { get; init; }
    public int TokensOut { get; init; }
    public string Model { get; init; } = string.Empty;
}

/// <summary>Raised by providers on non-retryable or exhausted-retry API failures.</summary>
public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);
