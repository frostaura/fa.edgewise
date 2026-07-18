using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Edgewise.Infrastructure.Llm;

/// <summary>
/// Registers the LLM gateway. Provider selection: OPENROUTER_API_KEY → OpenRouterProvider
/// (provider of choice); else ANTHROPIC_API_KEY → AnthropicProvider (secondary); else
/// NullLlmProvider (deterministic-only degradation).
/// </summary>
public sealed class LlmServiceModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        var options = LlmOptions.From(config);
        services.AddSingleton(options);

        if (options.HasOpenRouterKey)
        {
            services.AddSingleton<ILlmProvider>(new OpenRouterProvider(options));
        }
        else if (options.HasAnthropicKey)
        {
            services.AddSingleton<ILlmProvider>(new AnthropicProvider(options));
        }
        else
        {
            services.AddSingleton<ILlmProvider, NullLlmProvider>();
        }

        services.AddScoped<LlmGateway>();
    }
}
