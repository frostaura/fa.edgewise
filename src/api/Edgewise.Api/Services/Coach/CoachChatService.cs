using System.Text.Json;
using System.Text.Json.Nodes;
using Edgewise.Domain.Engines.Coach;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Data;
using Edgewise.Infrastructure.Llm;
using Edgewise.Infrastructure.Llm.Validation;
using Microsoft.EntityFrameworkCore;

namespace Edgewise.Api.Services.Coach;

/// <summary>
/// Ask-my-journal chat: a tool-use loop against Anthropic streamed to the client as SSE.
/// Wire protocol (one JSON object per SSE data line):
///   {type:"tool", name}      — a tool call is being executed
///   {type:"delta", text}     — a chunk of the answer text
///   {type:"done", insightId} — answer persisted as a ChatAnswer insight
///   {type:"offline", reason} — no LLM available (no key / opted out / over budget)
/// </summary>
public sealed class CoachChatService(EdgewiseDbContext db, LlmGateway gateway)
{
    private const int MaxToolIterations = 6;

    private static readonly string ChatSystemPrompt = """
        You are the Edgewise trading coach, answering questions about the user's OWN journal,
        trades and statistics using the provided tools. Hard rules:
        - NEVER advise, recommend or instruct any market action (no buy/sell/enter/exit calls).
          You explain what the user's data shows; you do not tell them what to trade.
        - Ground every claim in tool results; if the data does not answer the question, say so.
        - Outcome-agnostic tone: grade process, not P&L.
        - Be concise: a short paragraph or a few bullet points.
        """;

    private readonly EdgewiseDbContext _db = db;
    private readonly LlmGateway _gateway = gateway;

    public async Task HandleAsync(HttpContext context, ChatRequest request, CancellationToken ct)
    {
        var response = context.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var status = await _gateway.GetStatusAsync(ct);
        if (!status.CanCallLlm)
        {
            var reason = !status.Enabled ? "no_api_key" : status.OptedOut ? "opted_out" : "over_budget";
            await EmitAsync(response, new { type = "offline", reason }, ct);
            return;
        }

        var userId = await _db.Users.Select(u => u.Id).SingleAsync(ct);
        var tools = new CoachChatTools(_db);

        var messages = new List<LlmMessage>();
        foreach (var m in request.History ?? [])
        {
            if (m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Text))
            {
                messages.Add(new LlmMessage { Role = m.Role, Text = m.Text });
            }
        }

        messages.Add(LlmMessage.User(request.Message));

        var usedTools = new List<string>();
        LlmResult? result = null;
        for (var i = 0; i <= MaxToolIterations; i++)
        {
            result = await _gateway.TryCompleteAsync(userId, "chat", new LlmRequest
            {
                Model = _gateway.Options.LargeModel,
                System = ChatSystemPrompt,
                Messages = messages,
                MaxTokens = 1500,
                Tools = CoachChatTools.Definitions,
            }, ct);

            if (result is null)
            {
                await EmitAsync(response, new { type = "offline", reason = "llm_unavailable" }, ct);
                return;
            }

            if (result.StopReason != "tool_use" || result.ToolCalls.Count == 0)
            {
                break;
            }

            var toolResults = new List<LlmToolResult>();
            foreach (var call in result.ToolCalls)
            {
                usedTools.Add(call.Name);
                await EmitAsync(response, new { type = "tool", name = call.Name }, ct);
                var toolOutput = await tools.ExecuteAsync(call.Name, call.InputJson, ct);
                toolResults.Add(new LlmToolResult(call.Id, toolOutput));
            }

            messages.Add(new LlmMessage { Role = "assistant", Text = result.Text, ToolCalls = result.ToolCalls });
            messages.Add(new LlmMessage { Role = "user", ToolResults = toolResults });
        }

        var answer = result?.Text.Trim() ?? string.Empty;
        if (answer.Length == 0)
        {
            answer = "I could not produce an answer from your data this time — try rephrasing the question.";
        }

        // Directive-gate the final text before storing or emitting done.
        if (SplitSentences(answer).Any(InsightValidator.IsDirective))
        {
            answer = "I can't give trade instructions — I coach process, not market calls. " +
                     (usedTools.Count > 0
                         ? $"What I can offer is your own data: I checked {string.Join(", ", usedTools.Distinct().Select(FriendlyToolName))}. " +
                           "Ask me what the numbers show and I'll walk you through them."
                         : "Ask me what your journal and stats show and I'll walk you through them.");
        }

        var citations = usedTools
            .Distinct()
            .Select(t => new CitationDto($"stat:{t}", FriendlyToolName(t)))
            .ToList();
        var content = new InsightContent
        {
            Observations = [new InsightItem(answer, [.. citations.Select(c => c.Ref)])],
        };
        var insight = new Insight
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = InsightType.ChatAnswer,
            ContentJson = CoachJson.SerializeContent(content),
            CitationsJson = CoachJson.SerializeCitations(citations),
            ModelTag = result?.Model is { Length: > 0 } m2 ? m2 : _gateway.Options.LargeModel,
            PromptVersion = InsightSchema.PromptVersion,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Insights.Add(insight);
        await _db.SaveChangesAsync(ct);

        foreach (var chunk in Chunk(answer, 60))
        {
            await EmitAsync(response, new { type = "delta", text = chunk }, ct);
        }

        await EmitAsync(response, new { type = "done", insightId = insight.Id }, ct);
    }

    private static string FriendlyToolName(string tool) => tool switch
    {
        "get_expectancy" => "expectancy",
        "get_trades" => "trades",
        "get_trade" => "trade detail",
        "get_adherence_trend" => "adherence trend",
        "get_r_distribution" => "R distribution",
        "get_tilt" => "tilt signature",
        "get_calibration" => "calibration",
        "get_ptr" => "plan-then-trade ratio",
        _ => tool,
    };

    private static IEnumerable<string> SplitSentences(string text) =>
        text.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }

    private static async Task EmitAsync(HttpResponse response, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, CoachJson.CamelCase);
        await response.WriteAsync($"data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
