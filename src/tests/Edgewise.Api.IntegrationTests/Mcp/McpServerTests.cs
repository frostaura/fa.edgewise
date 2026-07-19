using System.Net.Http.Json;
using System.Text.Json;
using Edgewise.Contracts.Me;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Mcp;

/// <summary>
/// The /mcp surface over real streamable HTTP: handshake, tools/list, tools/call
/// with PAT (ew_) authentication, per-user scoping and friendly error payloads.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class McpServerTests(TestAppFactory factory)
{
    private static readonly string[] ExpectedTools =
    [
        "journal_list_trades", "journal_get_trade", "journal_create_plan", "journal_quick_log",
        "analytics_expectancy", "analytics_adherence_trend", "analytics_ptr",
        "portfolio_summary", "portfolio_ladder_state",
        "market_get_quote", "market_get_bars",
    ];

    private async Task<string> RegisterUserWithPatAsync()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var response = await client.PostAsJsonAsync("/api/me/tokens", new CreateApiTokenRequest("mcp-test"));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateApiTokenResponse>();
        created.ShouldNotBeNull();
        created.Token.ShouldStartWith("ew_");
        return created.Token;
    }

    private async Task<McpClient> CreateMcpClientAsync(string? bearerToken)
    {
        var httpClient = factory.CreateClient();
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        };
        if (bearerToken is not null)
        {
            options.AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {bearerToken}",
            };
        }

        var transport = new HttpClientTransport(options, httpClient, loggerFactory: null, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult result)
    {
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        text.ShouldNotBeNullOrWhiteSpace();
        return text;
    }

    private static JsonElement Parse(CallToolResult result) =>
        JsonDocument.Parse(Text(result)).RootElement;

    [Fact]
    public async Task Handshake_and_tools_list_expose_the_full_tool_surface()
    {
        var pat = await RegisterUserWithPatAsync();
        await using var mcp = await CreateMcpClientAsync(pat);

        var tools = await mcp.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();

        foreach (var expected in ExpectedTools)
        {
            names.ShouldContain(expected);
        }

        // Agent-facing API: every tool must carry a description.
        foreach (var tool in tools.Where(t => ExpectedTools.Contains(t.Name)))
        {
            tool.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Mcp_endpoint_rejects_unauthenticated_clients()
    {
        await Should.ThrowAsync<Exception>(async () =>
        {
            await using var mcp = await CreateMcpClientAsync(bearerToken: null);
            await mcp.ListToolsAsync();
        });
    }

    [Fact]
    public async Task Quick_log_list_trades_and_portfolio_summary_are_user_scoped()
    {
        var patA = await RegisterUserWithPatAsync();
        var patB = await RegisterUserWithPatAsync();

        await using var mcpA = await CreateMcpClientAsync(patA);
        await using var mcpB = await CreateMcpClientAsync(patB);

        // User A journals an execution through the MCP surface itself.
        var logged = Parse(await mcpA.CallToolAsync("journal_quick_log", new Dictionary<string, object?>
        {
            ["instrumentSymbol"] = "BTC-USD",
            ["side"] = "buy",
            ["qty"] = 0.5m,
            ["price"] = 50_000m,
        }));
        logged.TryGetProperty("error", out _).ShouldBeFalse(logged.ToString());
        logged.GetProperty("tradeId").GetGuid().ShouldNotBe(Guid.Empty);
        logged.GetProperty("fill").GetProperty("matchStatus").GetString().ShouldBe("confessed");

        // A sees exactly the trade just logged.
        var listA = Parse(await mcpA.CallToolAsync("journal_list_trades", new Dictionary<string, object?>()));
        listA.GetProperty("totalCount").GetInt64().ShouldBe(1);
        var trade = listA.GetProperty("trades").EnumerateArray().Single();
        trade.GetProperty("symbol").GetString().ShouldBe("BTC-USD");
        trade.GetProperty("status").GetString().ShouldBe("open");
        trade.GetProperty("qty").GetDecimal().ShouldBe(0.5m);

        // The composite detail resolves over MCP too.
        var detail = Parse(await mcpA.CallToolAsync("journal_get_trade", new Dictionary<string, object?>
        {
            ["tradeId"] = trade.GetProperty("id").GetString(),
        }));
        detail.GetProperty("fills").GetArrayLength().ShouldBe(1);

        // User B's PAT must not see A's trades (global query filter scoping).
        var listB = Parse(await mcpB.CallToolAsync("journal_list_trades", new Dictionary<string, object?>()));
        listB.GetProperty("totalCount").GetInt64().ShouldBe(0);

        // Portfolio summary answers with the caller's own (seeded) buckets.
        var summary = Parse(await mcpA.CallToolAsync("portfolio_summary", new Dictionary<string, object?>()));
        summary.GetProperty("baseCurrency").GetString().ShouldNotBeNullOrWhiteSpace();
        summary.GetProperty("perBucket").GetArrayLength().ShouldBeGreaterThan(0);

        var ladder = Parse(await mcpA.CallToolAsync("portfolio_ladder_state", new Dictionary<string, object?>()));
        ladder.GetProperty("state").GetString().ShouldBe("normal");

        // Analytics tools answer cleanly even with no closed trades yet.
        var ptr = Parse(await mcpA.CallToolAsync("analytics_ptr", new Dictionary<string, object?>()));
        ptr.TryGetProperty("weeks", out var weeks).ShouldBeTrue();
        weeks.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task Missing_entities_and_bad_arguments_produce_helpful_errors_not_exception_dumps()
    {
        var pat = await RegisterUserWithPatAsync();
        await using var mcp = await CreateMcpClientAsync(pat);

        // Unknown trade id: friendly not-found payload.
        var missingTrade = Parse(await mcp.CallToolAsync("journal_get_trade", new Dictionary<string, object?>
        {
            ["tradeId"] = Guid.NewGuid().ToString(),
        }));
        missingTrade.GetProperty("error").GetString().ShouldBe("trade_not_found");
        missingTrade.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();

        // Malformed id: told what shape is expected, no stack trace.
        var badId = Parse(await mcp.CallToolAsync("journal_get_trade", new Dictionary<string, object?>
        {
            ["tradeId"] = "not-a-guid",
        }));
        badId.GetProperty("error").GetString().ShouldBe("invalid_tradeId");

        // Unknown instrument symbol: names the symbol and suggests the fix.
        var missingInstrument = Parse(await mcp.CallToolAsync("market_get_quote", new Dictionary<string, object?>
        {
            ["symbol"] = "NOPE-XYZ",
        }));
        missingInstrument.GetProperty("error").GetString().ShouldBe("instrument_not_found");
        missingInstrument.GetProperty("message").GetString()!.ShouldContain("NOPE-XYZ");

        // Invalid enum argument lists the accepted values.
        var badGroupBy = Parse(await mcp.CallToolAsync("analytics_expectancy", new Dictionary<string, object?>
        {
            ["groupBy"] = "starSign",
        }));
        badGroupBy.GetProperty("error").GetString().ShouldBe("invalid_groupBy");
        badGroupBy.GetProperty("message").GetString()!.ShouldContain("instrument");
    }

    [Fact]
    public async Task Create_plan_journals_a_draft_without_executing_anything()
    {
        var pat = await RegisterUserWithPatAsync();
        await using var mcp = await CreateMcpClientAsync(pat);

        var created = Parse(await mcp.CallToolAsync("journal_create_plan", new Dictionary<string, object?>
        {
            ["instrumentSymbol"] = "ETH-USD",
            ["direction"] = "long",
            ["setupTag"] = "trend-pullback",
            ["trigger"] = "H4 close back above the 20EMA",
            ["stopPrice"] = 2_500m,
            ["targetNote"] = "half at 2R, trail the rest",
            ["sizeQty"] = 1.25m,
            ["invalidationNote"] = "daily close below 2450",
        }));

        created.TryGetProperty("error", out _).ShouldBeFalse(created.ToString());
        var plan = created.GetProperty("plan");
        plan.GetProperty("status").GetString().ShouldBe("draft");
        plan.GetProperty("stopPrice").GetDecimal().ShouldBe(2_500m);
        plan.GetProperty("sizeQty").GetDecimal().ShouldBe(1.25m);
        plan.GetProperty("instrument").GetProperty("symbol").GetString().ShouldBe("ETH-USD");
        created.GetProperty("message").GetString()!.ShouldContain("Nothing was executed");
    }
}
