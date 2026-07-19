using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Lab;

[Collection(ApiCollection.Name)]
public sealed class PipelineTests(TestAppFactory factory)
{
    private async Task<(HttpClient Client, Guid UserId, Guid StrategyId)> SetupAsync()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        var create = await client.PostAsJsonAsync("/api/strategies", new
        {
            name = "Pipeline target",
            ruleTree = LabTestData.RuleTree(),
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var strategyId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (client, auth.User!.Id, strategyId);
    }

    private static async Task<JsonElement> TransitionAsync(
        HttpClient client, Guid strategyId, string toState, bool force = false, string? reason = null,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/strategies/{strategyId}/transition",
            new { toState, force, reason });
        response.StatusCode.ShouldBe(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Gates_block_unforced_promotions_and_forward_jumps()
    {
        var (client, _, strategyId) = await SetupAsync();

        // Draft -> Backtested without a Done honest backtest: blocked.
        var blocked = await TransitionAsync(client, strategyId, "backtested",
            expected: HttpStatusCode.UnprocessableEntity);
        blocked.GetProperty("error").GetProperty("code").GetString().ShouldBe("gate_not_met");

        // Draft -> Paper (two stages forward): invalid.
        var jump = await TransitionAsync(client, strategyId, "paper", expected: HttpStatusCode.BadRequest);
        jump.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_transition");

        // Forcing without a reason is rejected.
        var noReason = await TransitionAsync(client, strategyId, "backtested", force: true,
            expected: HttpStatusCode.BadRequest);
        noReason.GetProperty("error").GetProperty("code").GetString().ShouldBe("reason_required");
    }

    [Fact]
    public async Task Force_with_reason_promotes_and_logs_evidence()
    {
        var (client, _, strategyId) = await SetupAsync();

        var pipeline = await TransitionAsync(client, strategyId, "backtested",
            force: true, reason: "manual review of external backtest");
        pipeline.GetProperty("state").GetString().ShouldBe("backtested");

        // Backtested -> Paper has no gate.
        pipeline = await TransitionAsync(client, strategyId, "paper");
        pipeline.GetProperty("state").GetString().ShouldBe("paper");

        // Paper -> TinyLive without 25 paper trades: blocked...
        var blocked = await TransitionAsync(client, strategyId, "tinyLive",
            expected: HttpStatusCode.UnprocessableEntity);
        blocked.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("25");

        // ...but force + reason works and the evidence snapshot is persisted.
        pipeline = await TransitionAsync(client, strategyId, "tinyLive",
            force: true, reason: "tiny size trial approved");
        pipeline.GetProperty("state").GetString().ShouldBe("tinyLive");

        var fetched = await client.GetFromJsonAsync<JsonElement>($"/api/strategies/{strategyId}/pipeline");
        var history = fetched.GetProperty("history");
        history.GetArrayLength().ShouldBe(3);

        var latest = history[0]; // newest first
        latest.GetProperty("fromState").GetString().ShouldBe("paper");
        latest.GetProperty("toState").GetString().ShouldBe("tinyLive");
        var evidence = latest.GetProperty("evidence");
        evidence.GetProperty("forced").GetBoolean().ShouldBeTrue();
        evidence.GetProperty("reason").GetString().ShouldBe("tiny size trial approved");
        evidence.GetProperty("gates").GetProperty("paperTrades").GetInt32().ShouldBe(0);
        evidence.GetProperty("gates").GetProperty("paperTradesRequired").GetInt32().ShouldBe(25);
    }

    [Fact]
    public async Task Paper_gate_passes_with_25_adherent_paper_trades()
    {
        var (client, userId, strategyId) = await SetupAsync();
        var instrumentId = await LabTestData.SeedInstrumentAsync(factory);

        await TransitionAsync(client, strategyId, "backtested", force: true, reason: "seed");
        await TransitionAsync(client, strategyId, "paper");

        await LabTestData.SeedStrategyTradesAsync(
            factory, userId, strategyId, instrumentId,
            count: 25, isPaper: true, adherenceScore: 95, rRealised: 0.4m);

        // Gate progress is visible...
        var pipeline = await client.GetFromJsonAsync<JsonElement>($"/api/strategies/{strategyId}/pipeline");
        var gates = pipeline.GetProperty("gates");
        gates.GetProperty("paperTrades").GetInt32().ShouldBe(25);
        gates.GetProperty("avgAdherence").GetDecimal().ShouldBe(95m);

        // ...and the unforced transition now succeeds.
        var promoted = await TransitionAsync(client, strategyId, "tinyLive");
        promoted.GetProperty("state").GetString().ShouldBe("tinyLive");
    }

    [Fact]
    public async Task Live_gate_needs_30_live_trades_with_positive_expectancy_and_back_is_free()
    {
        var (client, userId, strategyId) = await SetupAsync();
        var instrumentId = await LabTestData.SeedInstrumentAsync(factory);

        await TransitionAsync(client, strategyId, "backtested", force: true, reason: "seed");
        await TransitionAsync(client, strategyId, "paper");
        await TransitionAsync(client, strategyId, "tinyLive", force: true, reason: "seed");

        // 30 live trades but NEGATIVE expectancy: blocked.
        await LabTestData.SeedStrategyTradesAsync(
            factory, userId, strategyId, instrumentId,
            count: 30, isPaper: false, adherenceScore: 95, rRealised: -0.2m);
        var blocked = await TransitionAsync(client, strategyId, "live",
            expected: HttpStatusCode.UnprocessableEntity);
        blocked.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("expectancy");

        // Moving backward is always allowed, from anywhere to anywhere earlier.
        var demoted = await TransitionAsync(client, strategyId, "draft");
        demoted.GetProperty("state").GetString().ShouldBe("draft");
    }
}
