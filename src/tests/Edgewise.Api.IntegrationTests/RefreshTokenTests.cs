using System.Net;
using System.Net.Http.Json;
using Edgewise.Contracts.Auth;
using Edgewise.Contracts.Common;
using Shouldly;

namespace Edgewise.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class RefreshTokenTests(TestAppFactory factory)
{
    [Fact]
    public async Task Refresh_rotates_token_and_detects_reuse()
    {
        var client = factory.CreateClient();
        var registered = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        var originalToken = registered.RefreshToken!;

        // Rotate: old token is revoked, a new one is issued.
        var rotateResponse = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(originalToken));
        rotateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = await rotateResponse.Content.ReadFromJsonAsync<AuthResponse>();
        rotated!.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        rotated.RefreshToken.ShouldNotBe(originalToken);
        rotated.AccessToken.ShouldNotBeNullOrWhiteSpace();

        // Reusing the revoked token is treated as theft.
        var reuseResponse = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(originalToken));
        reuseResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var reuseError = await reuseResponse.Content.ReadFromJsonAsync<ErrorResponse>();
        reuseError!.Error.Code.ShouldBe("refresh_token_reused");

        // ...which revokes the whole family, including the newest token.
        var familyResponse = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(rotated.RefreshToken!));
        familyResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_with_unknown_token_returns_401()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest("not-a-real-token"));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        error!.Error.Code.ShouldBe("invalid_refresh_token");
    }
}
