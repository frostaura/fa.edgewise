using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Edgewise.Infrastructure.Llm;

/// <summary>
/// Registers the LLM gateway. ILlmProvider is AnthropicProvider when ANTHROPIC_API_KEY is
/// present, NullLlmProvider otherwise (deterministic-only mode).
/// </summary>
public sealed class LlmServiceModule : IServiceModule
{
    public void Configure(IServiceCollection services, IConfiguration config)
    {
        var options = LlmOptions.From(config);
        services.AddSingleton(options);

        if (options.HasApiKey)
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
