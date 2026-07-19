using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Edgewise.Api.Services.Journal;
using Edgewise.Domain.Entities;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Journal;

/// <summary>Smoke over every /api/analytics surface with a couple of closed trades.</summary>
[Collection(ApiCollection.Name)]
public sealed class AnalyticsSmokeTests(TestAppFactory factory)
{
    private static readonly JsonSerializerOptions Json = JournalTestHelpers.Json;

    [Fact]
    public async Task Analytics_endpoints_return_engine_shapes_over_closed_trades()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        var lookups = (await client.GetFromJsonAsync<PlanLookupsDto>("/api/plans/lookups", Json))!;
        var bucket = lookups.Buckets.Single(b => b.Kind == BucketKind.Trading);
        var instrument = lookups.Instruments.First(i => i.Symbol == "BTC-USD");
        await factory.SeedSnapshotAsync(userId, bucket.Id, 1_000_000);

        // One planned winner, one unplanned loser.
        async Task CloseTradeAsync(decimal entry, decimal exit, bool planned, string setup)
        {
            Guid tradeId;
            var at = DateTime.UtcNow.AddMinutes(1);
            if (planned)
            {
                var plan = (await (await client.PostAsJsonAsync("/api/plans", new CreatePlanRequest(
                        null, instrument.Id, bucket.Id, null, TradeDirection.Long, setup, "trigger",
                        entry - 5m, null, 1m, false, null, false, "true", null, entry), Json))
                    .Content.ReadFromJsonAsync<PlanDto>(Json))!;
                var trade = (await (await client.PostAsJsonAsync($"/api/plans/{plan.Id}/promote",
                        new PromotePlanRequest(null, [new PromoteFillRequest(null, FillSide.Buy, 1m, entry, 0, at)]), Json))
                    .Content.ReadFromJsonAsync<TradeDto>(Json))!;
                tradeId = trade.Id;
            }
            else
            {
                var fill = (await (await client.PostAsJsonAsync("/api/fills", new CreateFillRequest(
                        null, instrument.Id, FillSide.Buy, 1m, entry, 0, null, at, null), Json))
                    .Content.ReadFromJsonAsync<FillDto>(Json))!;
                var confessed = (await (await client.PostAsJsonAsync($"/api/fills/{fill.Id}/confess", new { }, Json))
                    .Content.ReadFromJsonAsync<FillDto>(Json))!;
                tradeId = confessed.TradeId!.Value;
            }

            (await client.PostAsJsonAsync($"/api/trades/{tradeId}/close",
                    new CloseTradeRequest(null, at.AddHours(1), exit, null), Json))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await CloseTradeAsync(100m, 110m, planned: true, "smoke-setup");
        await CloseTradeAsync(50m, 45m, planned: false, "unplanned");

        // Expectancy by setup: two groups, n flags present, low-sample flagged.
        var expectancy = await client.GetFromJsonAsync<JsonElement>("/api/analytics/expectancy?groupBy=setup", Json);
        var groups = expectancy.GetProperty("groups");
        groups.GetArrayLength().ShouldBe(2);
        groups[0].GetProperty("lowSample").GetBoolean().ShouldBeTrue();
        groups.EnumerateArray().Select(g => g.GetProperty("n").GetInt32()).Sum().ShouldBe(2);

        // R distribution has bins over trades with realised R (the planned one).
        var rDist = await client.GetFromJsonAsync<JsonElement>("/api/analytics/r-distribution", Json);
        rDist.GetProperty("n").GetInt32().ShouldBe(1);
        rDist.GetProperty("bins").GetArrayLength().ShouldBeGreaterThan(0);

        // The rest respond 200 with their engine shapes.
        foreach (var (url, property) in new (string, string?)[]
                 {
                     ("/api/analytics/drawdown", "series"),
                     ("/api/analytics/calendar-heatmap", null),
                     ("/api/analytics/sessions", null),
                     ("/api/analytics/tilt", "nAfterLoss"),
                     ("/api/analytics/ptr", null),
                     ("/api/analytics/adherence-trend", null),
                     ("/api/analytics/bias-cards", "cards"),
                 })
        {
            var response = await client.GetAsync(url);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, url);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
            if (property is not null)
            {
                body.TryGetProperty(property, out _).ShouldBeTrue($"{url} should expose {property}");
            }
        }

        // PTR covers both trades: 50% planned.
        var ptr = await client.GetFromJsonAsync<JsonElement>("/api/analytics/ptr", Json);
        ptr.GetArrayLength().ShouldBeGreaterThan(0);
        var week = ptr[0];
        week.GetProperty("n").GetInt32().ShouldBe(2);
        week.GetProperty("plannedN").GetInt32().ShouldBe(1);

        // Adherence rubric endpoint serves the engine's rules verbatim.
        var rubric = await client.GetFromJsonAsync<JsonElement>("/api/adherence/rubric", Json);
        rubric.GetProperty("version").GetString().ShouldBe("v1");
        rubric.GetProperty("rules").GetArrayLength().ShouldBe(9);

        // Recompute appends an adherence result (history preserved).
        var trades = await client.GetFromJsonAsync<Contracts.Common.PagedResult<TradeListItemDto>>("/api/trades", Json);
        var target = trades!.Items.First(t => t.PlanId != null);
        var recompute = await client.PostAsJsonAsync($"/api/adherence/trades/{target.Id}/recompute", new { }, Json);
        recompute.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await client.GetFromJsonAsync<TradeDetailDto>($"/api/trades/{target.Id}", Json);
        detail!.AdherenceHistory.Count.ShouldBe(2);

        // Decisions round trip.
        (await client.PostAsJsonAsync("/api/decisions",
                new CreateDecisionRequest(DecisionKind.Skip, instrument.Id, null, "Spread too wide"), Json))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var decisions = await client.GetFromJsonAsync<List<DecisionDto>>("/api/decisions?kind=skip", Json);
        decisions!.ShouldContain(d => d.Reason == "Spread too wide");
    }
}
