using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polly;
using Polly.Retry;

namespace Edgewise.Infrastructure.Llm;

/// <summary>
/// OpenRouter chat-completions client (POST https://openrouter.ai/api/v1/chat/completions),
/// OpenAI-style payloads: messages [{role, content}], tools as function definitions,
/// tool_calls with JSON-string arguments, usage.prompt_tokens/completion_tokens.
/// Retries 429/5xx three times with exponential backoff.
/// </summary>
public sealed class OpenRouterProvider(LlmOptions options) : ILlmProvider
{
    public const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static readonly AsyncRetryPolicy<HttpResponseMessage> RetryPolicy = Policy
        .Handle<HttpRequestException>()
        .OrResult<HttpResponseMessage>(r =>
            r.StatusCode == HttpStatusCode.TooManyRequests || (int)r.StatusCode >= 500)
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));

    private readonly LlmOptions _options = options;

    public bool IsConfigured => _options.HasOpenRouterKey;
    public string Name => "openrouter";

    public async Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct)
    {
        if (!_options.HasOpenRouterKey)
        {
            return new LlmResult { IsOffline = true };
        }

        var body = BuildBody(req);

        HttpResponseMessage response;
        try
        {
            response = await RetryPolicy.ExecuteAsync(async token =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                message.Headers.Add("Authorization", $"Bearer {_options.OpenRouterApiKey}");
                message.Headers.Add("HTTP-Referer", "https://edgewise.frostaura.net");
                message.Headers.Add("X-Title", "Edgewise");
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return await Http.SendAsync(message, token);
            }, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException("OpenRouter request failed after retries.", ex);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException($"OpenRouter returned {(int)response.StatusCode}: {Truncate(payload)}");
            }

            return ParseResponse(payload);
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];

    private string BuildBody(LlmRequest req)
    {
        var messages = new JsonArray();

        var system = req.System;
        if (req.JsonMode)
        {
            system = (system is null ? "" : system + "\n\n")
                + "Respond with a single valid JSON document only — no markdown fences, no prose.";
        }

        if (!string.IsNullOrWhiteSpace(system))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        foreach (var m in req.Messages)
        {
            if (m.ToolResults is { Count: > 0 })
            {
                // OpenAI style: one role:"tool" message per result.
                foreach (var result in m.ToolResults)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.CallId,
                        ["content"] = result.Content,
                    });
                }

                continue;
            }

            var message = new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = m.Text ?? string.Empty,
            };

            if (m.ToolCalls is { Count: > 0 })
            {
                var toolCalls = new JsonArray();
                foreach (var call in m.ToolCalls)
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.InputJson,
                        },
                    });
                }

                message["tool_calls"] = toolCalls;
            }

            messages.Add(message);
        }

        var bodyObject = new JsonObject
        {
            ["model"] = req.Model,
            ["max_tokens"] = req.MaxTokens,
            ["messages"] = messages,
        };

        if (req.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in req.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = tool.InputSchema.DeepClone(),
                    },
                });
            }

            bodyObject["tools"] = tools;
            bodyObject["tool_choice"] = "auto";
        }

        return bodyObject.ToJsonString();
    }

    private static LlmResult ParseResponse(string payload)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new LlmException("OpenRouter returned malformed JSON.", ex);
        }

        var choice = (root?["choices"] as JsonArray)?.FirstOrDefault();
        if (choice is null)
        {
            throw new LlmException($"OpenRouter returned no choices: {Truncate(payload)}");
        }

        var message = choice["message"];
        var text = message?["content"]?.GetValue<string>() ?? string.Empty;

        var toolCalls = new List<LlmToolCall>();
        if (message?["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls)
            {
                var function = call?["function"];
                if (function is null)
                {
                    continue;
                }

                toolCalls.Add(new LlmToolCall
                {
                    Id = call!["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                    Name = function["name"]?.GetValue<string>() ?? string.Empty,
                    InputJson = function["arguments"]?.GetValue<string>() is { Length: > 0 } args ? args : "{}",
                });
            }
        }

        var finishReason = choice["finish_reason"]?.GetValue<string>() ?? string.Empty;
        var usage = root!["usage"];
        return new LlmResult
        {
            Text = text,
            ToolCalls = toolCalls,
            StopReason = toolCalls.Count > 0 || finishReason == "tool_calls" ? "tool_use" : finishReason,
            TokensIn = usage?["prompt_tokens"]?.GetValue<int>() ?? 0,
            TokensOut = usage?["completion_tokens"]?.GetValue<int>() ?? 0,
            Model = root["model"]?.GetValue<string>() ?? string.Empty,
        };
    }
}
