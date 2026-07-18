using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Coach;

[Collection(ApiCollection.Name)]
public class PostMortemTests(TestAppFactory factory)
{
    [Fact]
    public async Task PostMortem_without_api_key_is_deterministic_with_citations()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        Guid tradeId;
        await using (var scoped = CoachTestData.OpenDb(factory, userId))
        {
            var instrument = await CoachTestData.SeedInstrumentAsync(scoped.Db);
            var seeded = await CoachTestData.SeedClosedTradeAsync(scoped.Db, userId, instrument.Id);
            tradeId = seeded.Trade.Id;
        }

        var response = await client.PostAsync($"/api/coach/trades/{tradeId}/postmortem", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var insight = await response.Content.ReadFromJsonAsync<JsonNode>();
        insight.ShouldNotBeNull();
        insight["type"]!.GetValue<string>().ShouldBe("postMortem");
        insight["modelTag"]!.GetValue<string>().ShouldBe("deterministic");
        insight["promptVersion"]!.GetValue<string>().ShouldBe("v1");
        insight["tradeId"]!.GetValue<Guid>().ShouldBe(tradeId);

        var observations = insight["content"]!["observations"]!.AsArray();
        observations.Count.ShouldBeGreaterThan(0);
        observations[0]!["citations"]!.AsArray().Count.ShouldBeGreaterThan(0);

        // Deviations come from the adherence deductions, cited to the adherence result.
        var deviations = insight["content"]!["deviations"]!.AsArray();
        deviations.Count.ShouldBeGreaterThan(0);
        deviations[0]!["citations"]!.AsArray()[0]!.GetValue<string>().ShouldStartWith("adherence:");

        // Resolved provenance refs are stored alongside.
        var citations = insight["citations"]!.AsArray();
        citations.Count.ShouldBeGreaterThan(0);
        citations.Select(c => c!["ref"]!.GetValue<string>())
            .ShouldContain(r => r.StartsWith("trade:"));
    }

    [Fact]
    public async Task Regenerating_supersedes_the_previous_postmortem()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        Guid tradeId;
        await using (var scoped = CoachTestData.OpenDb(factory, auth.User!.Id))
        {
            var instrument = await CoachTestData.SeedInstrumentAsync(scoped.Db);
            tradeId = (await CoachTestData.SeedClosedTradeAsync(scoped.Db, auth.User!.Id, instrument.Id)).Trade.Id;
        }

        var first = await (await client.PostAsync($"/api/coach/trades/{tradeId}/postmortem", null))
            .Content.ReadFromJsonAsync<JsonNode>();
        var second = await (await client.PostAsync($"/api/coach/trades/{tradeId}/postmortem", null))
            .Content.ReadFromJsonAsync<JsonNode>();

        second!["id"]!.GetValue<Guid>().ShouldNotBe(first!["id"]!.GetValue<Guid>());

        // Listing returns only the non-superseded insight.
        var list = await client.GetFromJsonAsync<JsonArray>(
            $"/api/coach/insights?type=postMortem&tradeId={tradeId}");
        list!.Count.ShouldBe(1);
        list[0]!["id"]!.GetValue<Guid>().ShouldBe(second["id"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task Feedback_roundtrip_stores_and_clears_reason()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        Guid tradeId;
        await using (var scoped = CoachTestData.OpenDb(factory, auth.User!.Id))
        {
            var instrument = await CoachTestData.SeedInstrumentAsync(scoped.Db);
            tradeId = (await CoachTestData.SeedClosedTradeAsync(scoped.Db, auth.User!.Id, instrument.Id)).Trade.Id;
        }

        var insight = await (await client.PostAsync($"/api/coach/trades/{tradeId}/postmortem", null))
            .Content.ReadFromJsonAsync<JsonNode>();
        var insightId = insight!["id"]!.GetValue<Guid>();

        var down = await client.PostAsJsonAsync(
            $"/api/coach/insights/{insightId}/feedback",
            new { feedback = "down", reason = "Missed the real issue" });
        down.StatusCode.ShouldBe(HttpStatusCode.OK);
        var downBody = await down.Content.ReadFromJsonAsync<JsonNode>();
        downBody!["feedback"]!.GetValue<string>().ShouldBe("down");
        downBody["feedbackReason"]!.GetValue<string>().ShouldBe("Missed the real issue");

        var up = await client.PostAsJsonAsync(
            $"/api/coach/insights/{insightId}/feedback", new { feedback = "up" });
        var upBody = await up.Content.ReadFromJsonAsync<JsonNode>();
        upBody!["feedback"]!.GetValue<string>().ShouldBe("up");
        upBody["feedbackReason"].ShouldBeNull();
    }

    [Fact]
    public async Task PostMortem_for_unknown_trade_is_404()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        var response = await client.PostAsync($"/api/coach/trades/{Guid.NewGuid()}/postmortem", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
