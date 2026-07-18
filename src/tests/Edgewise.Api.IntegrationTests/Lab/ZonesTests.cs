using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Lab;

[Collection(ApiCollection.Name)]
public sealed class ZonesTests(TestAppFactory factory)
{
    [Fact]
    public async Task Zones_crud_roundtrip()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var instrumentId = await LabTestData.SeedInstrumentAsync(factory);

        // Create.
        var create = await client.PostAsJsonAsync("/api/zones", new
        {
            instrumentId,
            priceLow = 95.5,
            priceHigh = 101.25,
            strength = 4,
            sourceTimeframe = "d1",
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var zone = await create.Content.ReadFromJsonAsync<JsonElement>();
        var zoneId = zone.GetProperty("id").GetGuid();
        zone.GetProperty("strength").GetInt32().ShouldBe(4);
        zone.GetProperty("sourceTimeframe").GetString().ShouldBe("d1");
        zone.GetProperty("archived").GetBoolean().ShouldBeFalse();

        // List filtered by instrument.
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/zones?instrumentId={instrumentId}");
        list.GetArrayLength().ShouldBe(1);

        // Update.
        var update = await client.PutAsJsonAsync($"/api/zones/{zoneId}", new { priceHigh = 103.0, strength = 5 });
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("priceHigh").GetDecimal().ShouldBe(103.0m);
        updated.GetProperty("strength").GetInt32().ShouldBe(5);

        // Invalid: high <= low.
        var bad = await client.PutAsJsonAsync($"/api/zones/{zoneId}", new { priceLow = 200.0 });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Archive toggle hides it from the default list.
        var archive = await client.PostAsync($"/api/zones/{zoneId}/archive", null);
        archive.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await archive.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("archived").GetBoolean().ShouldBeTrue();
        (await client.GetFromJsonAsync<JsonElement>($"/api/zones?instrumentId={instrumentId}"))
            .GetArrayLength().ShouldBe(0);
        (await client.GetFromJsonAsync<JsonElement>($"/api/zones?instrumentId={instrumentId}&includeArchived=true"))
            .GetArrayLength().ShouldBe(1);

        // Delete.
        (await client.DeleteAsync($"/api/zones/{zoneId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<JsonElement>($"/api/zones?instrumentId={instrumentId}&includeArchived=true"))
            .GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Zones_are_isolated_between_users()
    {
        var clientA = factory.CreateClient();
        var authA = await clientA.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientA.UseBearer(authA.AccessToken!);

        var clientB = factory.CreateClient();
        var authB = await clientB.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientB.UseBearer(authB.AccessToken!);

        var instrumentId = await LabTestData.SeedInstrumentAsync(factory);
        var create = await clientA.PostAsJsonAsync("/api/zones", new
        {
            instrumentId,
            priceLow = 10.0,
            priceHigh = 12.0,
            strength = 3,
            sourceTimeframe = "h4",
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var zoneId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // B sees nothing and cannot touch A's zone.
        (await clientB.GetFromJsonAsync<JsonElement>($"/api/zones?instrumentId={instrumentId}"))
            .GetArrayLength().ShouldBe(0);
        (await clientB.DeleteAsync($"/api/zones/{zoneId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clientB.PostAsync($"/api/zones/{zoneId}/archive", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A still has it.
        (await clientA.GetFromJsonAsync<JsonElement>($"/api/zones?instrumentId={instrumentId}"))
            .GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Zone_validation_rejects_bad_input()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var instrumentId = await LabTestData.SeedInstrumentAsync(factory);

        // strength out of range
        (await client.PostAsJsonAsync("/api/zones", new
        {
            instrumentId,
            priceLow = 1.0,
            priceHigh = 2.0,
            strength = 6,
            sourceTimeframe = "d1",
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // unknown instrument
        (await client.PostAsJsonAsync("/api/zones", new
        {
            instrumentId = Guid.NewGuid(),
            priceLow = 1.0,
            priceHigh = 2.0,
            strength = 3,
            sourceTimeframe = "d1",
        })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
