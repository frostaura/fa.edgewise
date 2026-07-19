using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Cockpit;

[Collection(ApiCollection.Name)]
public sealed class WatchlistTests(TestAppFactory factory)
{
    [Fact]
    public async Task Watchlist_crud_items_and_promote_flow()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        Guid instrumentId;
        await using (var db = factory.CreateDbContext(null))
        {
            instrumentId = db.Instruments.First(i => i.Symbol == "BTC-USD").Id;
        }

        // Create list
        var created = await client.PostAsJsonAsync("/api/watchlists", new { name = "Crypto" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var listId = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();

        // Empty name rejected
        (await client.PostAsJsonAsync("/api/watchlists", new { name = " " }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Add item
        var itemResponse = await client.PostAsJsonAsync($"/api/watchlists/{listId}/items", new
        {
            instrumentId,
            note = "watch the retest",
            whyWatching = "breakout over 60k",
        });
        itemResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var itemId = (await itemResponse.ReadJsonAsync()).GetProperty("id").GetGuid();

        // Unknown instrument rejected
        (await client.PostAsJsonAsync($"/api/watchlists/{listId}/items", new { instrumentId = Guid.NewGuid() }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // List aggregates items with symbol + alert count
        var lists = await (await client.GetAsync("/api/watchlists")).ReadJsonAsync();
        var list = lists.EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == listId);
        list.GetProperty("name").GetString().ShouldBe("Crypto");
        var item = list.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("symbol").GetString().ShouldBe("BTC-USD");
        item.GetProperty("whyWatching").GetString().ShouldBe("breakout over 60k");
        item.GetProperty("alertCount").GetInt32().ShouldBeGreaterThanOrEqualTo(0);

        // Rename
        var renamed = await client.PatchAsJsonAsync($"/api/watchlists/{listId}", new { name = "Majors" });
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Update item note
        var patched = await client.PatchAsJsonAsync(
            $"/api/watchlists/items/{itemId}", new { instrumentId, note = "updated note" });
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await patched.ReadJsonAsync()).GetProperty("note").GetString().ShouldBe("updated note");

        // Promote returns the prefill payload
        var promoted = await client.PostAsync($"/api/watchlists/items/{itemId}/promote-to-plan", null);
        promoted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var prefill = await promoted.ReadJsonAsync();
        prefill.GetProperty("instrumentId").GetGuid().ShouldBe(instrumentId);
        prefill.GetProperty("note").GetString()!.ShouldContain("breakout over 60k");

        // Delete item then list
        (await client.DeleteAsync($"/api/watchlists/items/{itemId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/watchlists/{listId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var after = await (await client.GetAsync("/api/watchlists")).ReadJsonAsync();
        after.EnumerateArray().Any(l => l.GetProperty("id").GetGuid() == listId).ShouldBeFalse();
    }

    [Fact]
    public async Task Watchlists_are_isolated_between_users()
    {
        var clientA = factory.CreateClient();
        var authA = await clientA.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientA.UseBearer(authA.AccessToken!);

        var clientB = factory.CreateClient();
        var authB = await clientB.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientB.UseBearer(authB.AccessToken!);

        var created = await clientA.PostAsJsonAsync("/api/watchlists", new { name = "A-only" });
        var listId = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();

        // B cannot see it
        var listsForB = await (await clientB.GetAsync("/api/watchlists")).ReadJsonAsync();
        listsForB.EnumerateArray().Any(l => l.GetProperty("id").GetGuid() == listId).ShouldBeFalse();

        // B cannot rename, add items to, or delete it
        (await clientB.PatchAsJsonAsync($"/api/watchlists/{listId}", new { name = "hijack" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.PostAsJsonAsync($"/api/watchlists/{listId}/items", new { instrumentId = Guid.NewGuid() }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.DeleteAsync($"/api/watchlists/{listId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Still intact for A
        var listsForA = await (await clientA.GetAsync("/api/watchlists")).ReadJsonAsync();
        listsForA.EnumerateArray()
            .Single(l => l.GetProperty("id").GetGuid() == listId)
            .GetProperty("name").GetString().ShouldBe("A-only");
    }
}
