using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Coach;

[Collection(ApiCollection.Name)]
public class CoachIsolationTests(TestAppFactory factory)
{
    [Fact]
    public async Task Insights_and_forecasts_are_invisible_across_users()
    {
        var clientA = factory.CreateClient();
        var authA = await clientA.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientA.UseBearer(authA.AccessToken!);

        var clientB = factory.CreateClient();
        var authB = await clientB.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientB.UseBearer(authB.AccessToken!);

        // User A: one post-mortem insight + one brier forecast.
        Guid tradeId;
        await using (var scoped = CoachTestData.OpenDb(factory, authA.User!.Id))
        {
            var instrument = await CoachTestData.SeedInstrumentAsync(scoped.Db);
            tradeId = (await CoachTestData.SeedClosedTradeAsync(scoped.Db, authA.User!.Id, instrument.Id)).Trade.Id;
        }

        var insight = await (await clientA.PostAsync($"/api/coach/trades/{tradeId}/postmortem", null))
            .Content.ReadFromJsonAsync<JsonNode>();
        var insightId = insight!["id"]!.GetValue<Guid>();
        var forecast = await (await clientA.PostAsJsonAsync("/api/brier", new
        {
            question = "Isolation test question?",
            resolutionDate = DateTime.UtcNow.AddDays(1),
            pUser = 0.4m,
            pMarket = 0.5m,
        })).Content.ReadFromJsonAsync<JsonNode>();
        var forecastId = forecast!["id"]!.GetValue<Guid>();

        // User B sees none of it.
        (await clientB.GetFromJsonAsync<JsonArray>("/api/coach/insights"))!.Count.ShouldBe(0);
        (await clientB.GetFromJsonAsync<JsonArray>("/api/brier"))!.Count.ShouldBe(0);
        (await clientB.GetAsync($"/api/brier/{forecastId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // User B cannot act on A's rows.
        (await clientB.PostAsJsonAsync($"/api/coach/insights/{insightId}/feedback", new { feedback = "up" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.PostAsync($"/api/coach/trades/{tradeId}/postmortem", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // User A still sees their own.
        (await clientA.GetFromJsonAsync<JsonArray>("/api/coach/insights"))!.Count.ShouldBeGreaterThan(0);
    }
}
