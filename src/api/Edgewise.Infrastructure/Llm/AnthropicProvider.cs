using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polly;
using Polly.Retry;

namespace Edgewise.Infrastructure.Llm;

/// <summary>
/// Raw-HTTP Anthropic Messages API client (POST https://api.anthropic.com/v1/messages).
/// Retries 429/5xx three times with exponential backoff (Polly). Supports plain text and
/// tool-use requests; parses text blocks, tool_use blocks and usage token counts.
/// </summary>
public sealed class AnthropicProvider(LlmOptions options) : ILlmProvider
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static readonly AsyncRetryPolicy<HttpResponseMessage> RetryPolicy = Policy
        .Handle<HttpRequestException>()
        .OrResult<HttpResponseMessage>(r =>
            r.StatusCode == HttpStatusCode.TooManyRequests || (int)r.StatusCode >= 500)
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));

    private readonly LlmOptions _options = options;

    public bool IsConfigured => _options.HasApiKey;
    public string Name => "anthropic";

    public async Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct)
    {
        if (!_options.HasApiKey)
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
                message.Headers.Add("x-api-key", _options.ApiKey);
                message.Headers.Add("anthropic-version", ApiVersion);
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return await Http.SendAsync(message, token);
            }, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException("Anthropic request failed after retries.", ex);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException($"Anthropic returned {(int)response.StatusCode}: {Truncate(payload)}");
            }

            return ParseResponse(payload);
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];

    private string BuildBody(LlmRequest req)
    {
        var messages = new JsonArray();
        foreach (var m in req.Messages)
        {
            var content = m.ContentBlocks is not null
                ? m.ContentBlocks.DeepClone()
                : JsonValue.Create(m.Text ?? string.Empty);
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = content });
        }

        var body = new JsonObject
        {
            ["model"] = req.Model,
            ["max_tokens"] = req.MaxTokens,
            ["messages"] = messages,
        };

        var system = req.System;
        if (req.JsonMode)
        {
            system = (system is null ? "" : system + "\n\n")
                + "Respond with a single valid JSON document only — no markdown fences, no prose.";
        }

        if (!string.IsNullOrWhiteSpace(system))
        {
            body["system"] = system;
        }

        if (req.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in req.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = tool.InputSchema.DeepClone(),
                });
            }

            body["tools"] = tools;
        }

        return body.ToJsonString();
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
            throw new LlmException("Anthropic returned malformed JSON.", ex);
        }

        if (root is not JsonObject obj)
        {
            throw new LlmException("Anthropic returned an unexpected payload shape.");
        }

        var text = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();
        var content = obj["content"] as JsonArray;
        if (content is not null)
        {
            foreach (var block in content)
            {
                switch (block?["type"]?.GetValue<string>())
                {
                    case "text":
                        text.Append(block["text"]?.GetValue<string>());
                        break;
                    case "tool_use":
                        toolCalls.Add(new LlmToolCall
                        {
                            Id = block["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                            Name = block["name"]?.GetValue<string>() ?? string.Empty,
                            InputJson = block["input"]?.ToJsonString() ?? "{}",
                        });
                        break;
                }
            }
        }

        var usage = obj["usage"] as JsonObject;
        return new LlmResult
        {
            Text = text.ToString(),
            ToolCalls = toolCalls,
            StopReason = obj["stop_reason"]?.GetValue<string>() ?? string.Empty,
            TokensIn = usage?["input_tokens"]?.GetValue<int>() ?? 0,
            TokensOut = usage?["output_tokens"]?.GetValue<int>() ?? 0,
            Model = obj["model"]?.GetValue<string>() ?? string.Empty,
            RawContent = content?.DeepClone(),
        };
    }
}
