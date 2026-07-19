using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Lab;

[Collection(ApiCollection.Name)]
public sealed class StrategiesTests(TestAppFactory factory)
{
    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        return client;
    }

    [Fact]
    public async Task Create_read_update_with_version_bump()
    {
        var client = await AuthedClientAsync();

        var create = await client.PostAsJsonAsync("/api/strategies", new
        {
            name = "MA cross test",
            ruleTree = LabTestData.RuleTree(),
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("state").GetString().ShouldBe("draft");
        created.GetProperty("currentVersion").GetInt32().ShouldBe(1);

        // Detail exposes the rule tree and version history.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/strategies/{id}");
        detail.GetProperty("ruleTree").GetProperty("direction").GetString().ShouldBe("long");
        detail.GetProperty("versions").GetArrayLength().ShouldBe(1);

        // Updating with a CHANGED tree bumps the version and keeps history immutable.
        var changedTree = LabTestData.SimpleRuleTree.Replace("\"operand\": 0", "\"operand\": 0.01");
        var update = await client.PutAsJsonAsync($"/api/strategies/{id}", new
        {
            ruleTree = LabTestData.RuleTree(changedTree),
        });
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("currentVersion").GetInt32().ShouldBe(2);
        updated.GetProperty("versions").GetArrayLength().ShouldBe(2);

        // Updating with an IDENTICAL tree does not bump.
        var same = await client.PutAsJsonAsync($"/api/strategies/{id}", new
        {
            ruleTree = LabTestData.RuleTree(changedTree),
        });
        (await same.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("currentVersion").GetInt32().ShouldBe(2);

        // Name-only update does not bump either.
        var rename = await client.PutAsJsonAsync($"/api/strategies/{id}", new { name = "Renamed" });
        var renamed = await rename.Content.ReadFromJsonAsync<JsonElement>();
        renamed.GetProperty("name").GetString().ShouldBe("Renamed");
        renamed.GetProperty("currentVersion").GetInt32().ShouldBe(2);

        // List includes it.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/strategies");
        list.EnumerateArray().ShouldContain(s => s.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Invalid_rule_tree_returns_400_with_field_errors()
    {
        var client = await AuthedClientAsync();

        // 'within' without operand2 + a bogus indicator + risk out of range.
        var invalid = """
            {
              "direction": "long",
              "conditions": [
                { "indicator": "rsiBand", "params": { "period": 14 }, "operator": "within", "operand": 40, "enabled": true },
                { "indicator": "notAnIndicator", "operator": "gt", "operand": 0 }
              ],
              "exits": { "trailMethod": "none", "stopAtrMult": 2 },
              "risk": { "riskPctPerTrade": 0 }
            }
            """;
        var response = await client.PostAsJsonAsync("/api/strategies", new
        {
            name = "Broken",
            ruleTree = LabTestData.RuleTree(invalid),
        });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = body.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("invalid_rule_tree");
        var fields = error.GetProperty("fields");
        fields.ValueKind.ShouldBe(JsonValueKind.Object);
        var keys = fields.EnumerateObject().Select(p => p.Name).ToList();
        keys.ShouldContain(k => k.Contains("conditions[1]")); // bad indicator
        keys.ShouldContain(k => k.Contains("risk"));          // riskPctPerTrade = 0
    }

    [Fact]
    public async Task Missing_operand2_for_within_is_a_semantic_field_error()
    {
        var client = await AuthedClientAsync();
        var invalid = """
            {
              "direction": "long",
              "conditions": [
                { "indicator": "rsiBand", "params": { "period": 14 }, "operator": "within", "operand": 40, "enabled": true }
              ],
              "exits": { "trailMethod": "none", "stopAtrMult": 2 },
              "risk": { "riskPctPerTrade": 0.5 }
            }
            """;
        var response = await client.PostAsJsonAsync("/api/strategies", new
        {
            name = "Broken within",
            ruleTree = LabTestData.RuleTree(invalid),
        });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("fields")
            .TryGetProperty("conditions[0].operand2", out var msg).ShouldBeTrue();
        msg.GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Templates_expose_four_playbook_rule_trees_that_validate()
    {
        var client = await AuthedClientAsync();
        var templates = await client.GetFromJsonAsync<JsonElement>("/api/strategies/templates");
        templates.GetArrayLength().ShouldBe(4);

        var ids = templates.EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        ids.ShouldBe(["trend-pullback", "breakout-retest", "mean-reversion", "momentum"], ignoreOrder: true);

        // Every template must be accepted by the strategy validator itself.
        foreach (var template in templates.EnumerateArray())
        {
            var response = await client.PostAsJsonAsync("/api/strategies", new
            {
                name = $"From template {template.GetProperty("id").GetString()}",
                ruleTree = template.GetProperty("ruleTree"),
            });
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
    }
}
