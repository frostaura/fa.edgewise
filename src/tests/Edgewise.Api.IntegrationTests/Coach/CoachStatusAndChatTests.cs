using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Edgewise.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Coach;

[Collection(ApiCollection.Name)]
public class CoachStatusAndChatTests(TestAppFactory factory)
{
    [Fact]
    public async Task Chat_without_api_key_emits_a_single_offline_sse_event()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        var response = await client.PostAsJsonAsync("/api/coach/chat", new
        {
            message = "What's my expectancy on breakout trades?",
            history = Array.Empty<object>(),
        });

        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("data: ");
        var payload = JsonNode.Parse(body.Split("data: ")[1].Split('\n')[0]);
        payload!["type"]!.GetValue<string>().ShouldBe("offline");
        payload["reason"]!.GetValue<string>().ShouldBe("no_api_key");
    }

    [Fact]
    public async Task Status_reports_budget_usage_and_budget_guard_state()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        // Tiny budget + existing usage this month → over budget.
        await using (var scoped = CoachTestData.OpenDb(factory, userId))
        {
            var user = await scoped.Db.Users.SingleAsync();
            user.MonthlyTokenBudget = 100;
            scoped.Db.LlmRequestLogs.Add(new LlmRequestLog
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Purpose = "postmortem",
                Model = "claude-haiku-4-5-20251001",
                TokensIn = 150,
                TokensOut = 80,
                CostMicroUsd = 550,
                At = DateTime.UtcNow,
                Success = true,
            });
            await scoped.Db.SaveChangesAsync();
        }

        var status = await client.GetFromJsonAsync<JsonNode>("/api/coach/status");
        status!["enabled"]!.GetValue<bool>().ShouldBeFalse(); // no ANTHROPIC_API_KEY in tests
        status["provider"]!.GetValue<string>().ShouldBe("none");
        status["budgetUsedTokens"]!.GetValue<long>().ShouldBe(230);
        status["budgetTokens"]!.GetValue<long>().ShouldBe(100);
        status["optedOut"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Over_budget_user_still_gets_deterministic_postmortems()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        Guid tradeId;
        await using (var scoped = CoachTestData.OpenDb(factory, userId))
        {
            var user = await scoped.Db.Users.SingleAsync();
            user.MonthlyTokenBudget = 1; // effectively exhausted immediately
            scoped.Db.LlmRequestLogs.Add(new LlmRequestLog
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Purpose = "chat",
                Model = "claude-sonnet-5",
                TokensIn = 10,
                TokensOut = 10,
                CostMicroUsd = 180,
                At = DateTime.UtcNow,
                Success = true,
            });
            await scoped.Db.SaveChangesAsync();

            var instrument = await CoachTestData.SeedInstrumentAsync(scoped.Db);
            tradeId = (await CoachTestData.SeedClosedTradeAsync(scoped.Db, userId, instrument.Id)).Trade.Id;
        }

        var insight = await (await client.PostAsync($"/api/coach/trades/{tradeId}/postmortem", null))
            .Content.ReadFromJsonAsync<JsonNode>();
        insight!["modelTag"]!.GetValue<string>().ShouldBe("deterministic");
    }

    [Fact]
    public async Task Opted_out_user_gets_offline_chat_with_reason()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        // Opt out via the existing PATCH /api/me endpoint.
        var patch = await client.PatchAsJsonAsync("/api/me/", new { llmOptOut = true });
        patch.EnsureSuccessStatusCode();

        var status = await client.GetFromJsonAsync<JsonNode>("/api/coach/status");
        status!["optedOut"]!.GetValue<bool>().ShouldBeTrue();
    }
}
