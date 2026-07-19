using System.Net;
using System.Net.Http.Json;
using Edgewise.Api.Endpoints;
using Edgewise.Contracts.Auth;
using Edgewise.Contracts.Common;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Settings;

[Collection(ApiCollection.Name)]
public sealed class ChangePasswordTests(TestAppFactory factory)
{
    [Fact]
    public async Task Change_password_rotates_credentials_and_revokes_refresh_tokens()
    {
        var client = factory.CreateClient();
        var email = ApiClientHelpers.UniqueEmail();
        var registered = await client.RegisterAsync(email);
        client.UseBearer(registered.AccessToken!);

        const string newPassword = "new-horse-battery-staple";
        var response = await client.PostAsJsonAsync(
            "/api/me/password", new ChangePasswordRequest(ApiClientHelpers.Password, newPassword));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Old password no longer works; the new one does.
        var oldLogin = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(email, ApiClientHelpers.Password));
        oldLogin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await client.LoginAsync(email, newPassword);

        // Every pre-change refresh token was revoked — other devices are logged out.
        var refresh = await client.PostAsJsonAsync(
            "/api/auth/refresh", new RefreshRequest(registered.RefreshToken!));
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Change_password_requires_the_correct_current_password()
    {
        var client = factory.CreateClient();
        var registered = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(registered.AccessToken!);

        var wrongCurrent = await client.PostAsJsonAsync(
            "/api/me/password", new ChangePasswordRequest("not-my-password", "another-fine-password"));
        wrongCurrent.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await wrongCurrent.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code.ShouldBe("invalid_current_password");

        var weak = await client.PostAsJsonAsync(
            "/api/me/password", new ChangePasswordRequest(ApiClientHelpers.Password, "short"));
        weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await weak.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code.ShouldBe("weak_password");

        // Failed attempts leave existing sessions intact.
        var refresh = await client.PostAsJsonAsync(
            "/api/auth/refresh", new RefreshRequest(registered.RefreshToken!));
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
