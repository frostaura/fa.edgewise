using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Coach;

[Collection(ApiCollection.Name)]
public class BrierTests(TestAppFactory factory)
{
    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        return client;
    }

    [Fact]
    public async Task Create_resolve_and_calibrate()
    {
        var client = await AuthedClientAsync();

        var create1 = await client.PostAsJsonAsync("/api/brier", new
        {
            question = "Will BTC close above 100k this Friday?",
            resolutionDate = DateTime.UtcNow.AddDays(3),
            pUser = 0.7m,
            pMarket = 0.6m,
            rulesUrl = "https://example.test/rules",
            makerTaker = "maker",
        });
        create1.StatusCode.ShouldBe(HttpStatusCode.Created);
        var f1 = await create1.Content.ReadFromJsonAsync<JsonNode>();

        var create2 = await client.PostAsJsonAsync("/api/brier", new
        {
            question = "Will the CPI print land below 3%?",
            resolutionDate = DateTime.UtcNow.AddDays(5),
            pUser = 0.2m,
            pMarket = 0.35m,
        });
        var f2 = await create2.Content.ReadFromJsonAsync<JsonNode>();

        // Resolve: first true (brier (0.7-1)^2 = 0.09), second false ((0.2-0)^2 = 0.04).
        var resolve1 = await client.PostAsJsonAsync($"/api/brier/{f1!["id"]}/resolve", new { outcome = true });
        resolve1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var resolved1 = await resolve1.Content.ReadFromJsonAsync<JsonNode>();
        resolved1!["brierScore"]!.GetValue<decimal>().ShouldBe(0.09m, 0.0001m);
        resolved1["outcome"]!.GetValue<bool>().ShouldBeTrue();
        resolved1["resolvedAt"].ShouldNotBeNull();

        var resolve2 = await client.PostAsJsonAsync($"/api/brier/{f2!["id"]}/resolve", new { outcome = false });
        (await resolve2.Content.ReadFromJsonAsync<JsonNode>())!["brierScore"]!
            .GetValue<decimal>().ShouldBe(0.04m, 0.0001m);

        // Double-resolve conflicts.
        var again = await client.PostAsJsonAsync($"/api/brier/{f1["id"]}/resolve", new { outcome = false });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Calibration report + deterministic insight.
        var calibration = await client.GetFromJsonAsync<JsonNode>("/api/brier/calibration");
        calibration!["report"]!["n"]!.GetValue<int>().ShouldBe(2);
        calibration["report"]!["meanBrier"]!.GetValue<decimal>().ShouldBe(0.065m, 0.0001m);
        calibration["insight"]!["type"]!.GetValue<string>().ShouldBe("calibration");
        calibration["insight"]!["modelTag"]!.GetValue<string>().ShouldBe("deterministic");
        calibration["insight"]!["content"]!["observations"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task List_filters_by_resolution_state_and_update_and_delete_work()
    {
        var client = await AuthedClientAsync();

        var created = await (await client.PostAsJsonAsync("/api/brier", new
        {
            question = "Will it rain tomorrow?",
            resolutionDate = DateTime.UtcNow.AddDays(1),
            pUser = 0.5m,
            pMarket = 0.5m,
        })).Content.ReadFromJsonAsync<JsonNode>();
        var id = created!["id"]!.GetValue<Guid>();

        var updated = await (await client.PutAsJsonAsync($"/api/brier/{id}", new { pUser = 0.55m }))
            .Content.ReadFromJsonAsync<JsonNode>();
        updated!["pUser"]!.GetValue<decimal>().ShouldBe(0.55m);

        var open = await client.GetFromJsonAsync<JsonArray>("/api/brier?resolved=false");
        open!.Select(f => f!["id"]!.GetValue<Guid>()).ShouldContain(id);

        var resolvedList = await client.GetFromJsonAsync<JsonArray>("/api/brier?resolved=true");
        resolvedList!.Select(f => f!["id"]!.GetValue<Guid>()).ShouldNotContain(id);

        (await client.DeleteAsync($"/api/brier/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/brier/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invalid_probability_is_rejected()
    {
        var client = await AuthedClientAsync();
        var response = await client.PostAsJsonAsync("/api/brier", new
        {
            question = "Bad probability",
            resolutionDate = DateTime.UtcNow.AddDays(1),
            pUser = 1.5m,
            pMarket = 0.5m,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
