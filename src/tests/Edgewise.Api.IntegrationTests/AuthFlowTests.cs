using System.Net;
using System.Net.Http.Json;
using Edgewise.Contracts.Auth;
using Edgewise.Contracts.Common;
using Shouldly;

namespace Edgewise.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthFlowTests(TestAppFactory factory)
{
    [Fact]
    public async Task Health_reports_ok_with_database_up()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("\"ok\"");
    }

    [Fact]
    public async Task Register_login_me_roundtrip()
    {
        var client = factory.CreateClient();
        var email = ApiClientHelpers.UniqueEmail();

        var registered = await client.RegisterAsync(email);
        registered.RequiresTotp.ShouldBeFalse();
        registered.AccessToken.ShouldNotBeNullOrWhiteSpace();
        registered.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        registered.User.ShouldNotBeNull();
        registered.User.Email.ShouldBe(email);
        registered.User.BaseCurrency.ShouldBe("ZAR");
        registered.User.Timezone.ShouldBe("Africa/Johannesburg");
        registered.User.TotpEnabled.ShouldBeFalse();

        var loggedIn = await client.LoginAsync(email);
        loggedIn.RequiresTotp.ShouldBeFalse();
        loggedIn.AccessToken.ShouldNotBeNullOrWhiteSpace();

        client.UseBearer(loggedIn.AccessToken!);
        var me = await client.GetFromJsonAsync<UserDto>("/api/me");
        me.ShouldNotBeNull();
        me.Email.ShouldBe(email);
        me.Id.ShouldBe(registered.User.Id);
    }

    [Fact]
    public async Task Register_with_duplicate_email_returns_conflict_error_shape()
    {
        var client = factory.CreateClient();
        var email = ApiClientHelpers.UniqueEmail();
        await client.RegisterAsync(email);

        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, ApiClientHelpers.Password));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        error.ShouldNotBeNull();
        error.Error.Code.ShouldBe("email_taken");
        error.Error.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Register_with_weak_password_returns_400()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest(ApiClientHelpers.UniqueEmail(), "short"));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        error!.Error.Code.ShouldBe("weak_password");
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var client = factory.CreateClient();
        var email = ApiClientHelpers.UniqueEmail();
        await client.RegisterAsync(email);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "wrong-password-123"));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        error!.Error.Code.ShouldBe("invalid_credentials");
    }

    [Fact]
    public async Task Me_without_token_returns_401_error_shape()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/me");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        error!.Error.Code.ShouldBe("unauthorized");
    }

    [Fact]
    public async Task Logout_revokes_refresh_token()
    {
        var client = factory.CreateClient();
        var registered = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(registered.RefreshToken!));
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(registered.RefreshToken!));
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
