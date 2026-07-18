using System.Net;
using System.Net.Http.Json;
using Edgewise.Contracts.Auth;
using Edgewise.Contracts.Common;
using OtpNet;
using Shouldly;

namespace Edgewise.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class TotpTests(TestAppFactory factory)
{
    [Fact]
    public async Task Totp_enable_step_up_login_and_recovery_code()
    {
        var client = factory.CreateClient();
        var email = ApiClientHelpers.UniqueEmail();
        var registered = await client.RegisterAsync(email);
        client.UseBearer(registered.AccessToken!);

        // Setup: get the shared secret + otpauth URI.
        var setupResponse = await client.PostAsync("/api/auth/totp/setup", null);
        setupResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var setup = await setupResponse.Content.ReadFromJsonAsync<TotpSetupResponse>();
        setup!.Secret.ShouldNotBeNullOrWhiteSpace();
        setup.OtpauthUri.ShouldStartWith("otpauth://totp/");
        setup.OtpauthUri.ShouldContain(Uri.EscapeDataString(email));

        // Enable with a real code computed from the secret.
        var totp = new Totp(Base32Encoding.ToBytes(setup.Secret));
        var enableResponse = await client.PostAsJsonAsync(
            "/api/auth/totp/enable", new TotpCodeRequest(totp.ComputeTotp()));
        enableResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var enabled = await enableResponse.Content.ReadFromJsonAsync<TotpEnableResponse>();
        enabled!.RecoveryCodes.Count.ShouldBe(8);
        enabled.RecoveryCodes.ShouldAllBe(c => c.Length == 9 && c[4] == '-');

        // Password login now requires the TOTP step.
        var login = await client.LoginAsync(email);
        login.RequiresTotp.ShouldBeTrue();
        login.AccessToken.ShouldBeNull();
        login.TotpToken.ShouldNotBeNullOrWhiteSpace();

        // Step-up with a TOTP code completes the login.
        var stepUpResponse = await client.PostAsJsonAsync(
            "/api/auth/login/totp", new TotpLoginRequest(login.TotpToken!, totp.ComputeTotp()));
        stepUpResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stepUp = await stepUpResponse.Content.ReadFromJsonAsync<AuthResponse>();
        stepUp!.AccessToken.ShouldNotBeNullOrWhiteSpace();
        stepUp.User!.TotpEnabled.ShouldBeTrue();

        // A recovery code also completes the login...
        var recoveryLogin = await client.LoginAsync(email);
        var recoveryCode = enabled.RecoveryCodes[0];
        var recoveryResponse = await client.PostAsJsonAsync(
            "/api/auth/login/totp", new TotpLoginRequest(recoveryLogin.TotpToken!, recoveryCode));
        recoveryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...but only once.
        var replayLogin = await client.LoginAsync(email);
        var replayResponse = await client.PostAsJsonAsync(
            "/api/auth/login/totp", new TotpLoginRequest(replayLogin.TotpToken!, recoveryCode));
        replayResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var replayError = await replayResponse.Content.ReadFromJsonAsync<ErrorResponse>();
        replayError!.Error.Code.ShouldBe("invalid_totp_code");
    }

    [Fact]
    public async Task Totp_step_up_token_cannot_be_used_as_access_token()
    {
        var client = factory.CreateClient();
        var email = ApiClientHelpers.UniqueEmail();
        var registered = await client.RegisterAsync(email);
        client.UseBearer(registered.AccessToken!);

        var setup = await (await client.PostAsync("/api/auth/totp/setup", null))
            .Content.ReadFromJsonAsync<TotpSetupResponse>();
        var totp = new Totp(Base32Encoding.ToBytes(setup!.Secret));
        await client.PostAsJsonAsync("/api/auth/totp/enable", new TotpCodeRequest(totp.ComputeTotp()));

        var login = await client.LoginAsync(email);
        var probe = factory.CreateClient();
        probe.UseBearer(login.TotpToken!);
        var response = await probe.GetAsync("/api/me");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Totp_disable_restores_password_only_login()
    {
        var client = factory.CreateClient();
        var email = ApiClientHelpers.UniqueEmail();
        var registered = await client.RegisterAsync(email);
        client.UseBearer(registered.AccessToken!);

        var setup = await (await client.PostAsync("/api/auth/totp/setup", null))
            .Content.ReadFromJsonAsync<TotpSetupResponse>();
        var totp = new Totp(Base32Encoding.ToBytes(setup!.Secret));
        await client.PostAsJsonAsync("/api/auth/totp/enable", new TotpCodeRequest(totp.ComputeTotp()));

        var disableResponse = await client.PostAsJsonAsync(
            "/api/auth/totp/disable", new TotpCodeRequest(totp.ComputeTotp()));
        disableResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var login = await client.LoginAsync(email);
        login.RequiresTotp.ShouldBeFalse();
        login.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }
}
