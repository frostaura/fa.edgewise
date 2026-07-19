namespace Edgewise.Infrastructure.Llm;

/// <summary>Minimal LLM completion surface. One implementation per backing provider.</summary>
public interface ILlmProvider
{
    /// <summary>True when the provider can actually reach a model (an API key is configured).</summary>
    bool IsConfigured { get; }

    /// <summary>Short provider tag surfaced by /api/coach/status ("anthropic" or "none").</summary>
    string Name { get; }

    Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct);
}

/// <summary>
/// Selected when neither OPENROUTER_API_KEY nor ANTHROPIC_API_KEY is present. Returns an
/// offline marker so orchestrators emit deterministic-only insights instead of calling out.
/// </summary>
public sealed class NullLlmProvider : ILlmProvider
{
    public bool IsConfigured => false;
    public string Name => "none";

    public Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct) =>
        Task.FromResult(new LlmResult { IsOffline = true });
}
