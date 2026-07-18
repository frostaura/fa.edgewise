using System.Net;
using System.Net.Http.Json;
using Edgewise.Contracts.Auth;
using Edgewise.Contracts.Me;
using Shouldly;

namespace Edgewise.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ApiTokenTests(TestAppFactory factory)
{
    [Fact]
    public async Task Pat_create_list_authenticate_and_revoke()
    {
        var client = factory.CreateClient();
        var registered = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(registered.AccessToken!);

        // Create: plaintext token shown exactly once.
        var createResponse = await client.PostAsJsonAsync("/api/me/tokens", new CreateApiTokenRequest("mcp-client"));
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateApiTokenResponse>();
        created!.Token.ShouldStartWith("ew_");
        created.Name.ShouldBe("mcp-client");

        // List: metadata only, no secret.
        var listed = await client.GetFromJsonAsync<List<ApiTokenDto>>("/api/me/tokens");
        listed!.ShouldContain(t => t.Id == created.Id && t.Name == "mcp-client");

        // The PAT authenticates API calls.
        var patClient = factory.CreateClient();
        patClient.UseBearer(created.Token);
        var me = await patClient.GetFromJsonAsync<UserDto>("/api/me");
        me!.Id.ShouldBe(registered.User!.Id);

        // LastUsedAt is tracked.
        var afterUse = await client.GetFromJsonAsync<List<ApiTokenDto>>("/api/me/tokens");
        afterUse!.Single(t => t.Id == created.Id).LastUsedAt.ShouldNotBeNull();

        // Delete: the PAT stops working.
        var deleteResponse = await client.DeleteAsync($"/api/me/tokens/{created.Id}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var afterDelete = await patClient.GetAsync("/api/me");
        afterDelete.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Deleting_another_users_token_returns_404()
    {
        var clientA = factory.CreateClient();
        var userA = await clientA.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientA.UseBearer(userA.AccessToken!);
        var created = await (await clientA.PostAsJsonAsync("/api/me/tokens", new CreateApiTokenRequest("a-token")))
            .Content.ReadFromJsonAsync<CreateApiTokenResponse>();

        var clientB = factory.CreateClient();
        var userB = await clientB.RegisterAsync(ApiClientHelpers.UniqueEmail());
        clientB.UseBearer(userB.AccessToken!);

        var response = await clientB.DeleteAsync($"/api/me/tokens/{created!.Id}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
