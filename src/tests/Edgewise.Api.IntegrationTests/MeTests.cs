using System.Net;
using System.Net.Http.Json;
using Edgewise.Contracts.Auth;
using Edgewise.Contracts.Common;
using Edgewise.Contracts.Me;
using Shouldly;

namespace Edgewise.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class MeTests(TestAppFactory factory)
{
    [Fact]
    public async Task Patch_me_updates_profile_fields()
    {
        var client = factory.CreateClient();
        var registered = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(registered.AccessToken!);

        var response = await client.PatchAsJsonAsync("/api/me", new UpdateMeRequest(
            BaseCurrency: "usd",
            Timezone: "Europe/London",
            LlmOptOut: true,
            SettingsJson: """{"theme":"dark"}"""));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var me = await client.GetFromJsonAsync<UserDto>("/api/me");
        me!.BaseCurrency.ShouldBe("USD");
        me.Timezone.ShouldBe("Europe/London");
        me.LlmOptOut.ShouldBeTrue();

        // jsonb storage canonicalizes whitespace, so compare parsed JSON.
        me.SettingsJson.ShouldNotBeNull();
        using var settings = System.Text.Json.JsonDocument.Parse(me.SettingsJson);
        settings.RootElement.GetProperty("theme").GetString().ShouldBe("dark");
    }

    [Fact]
    public async Task Patch_me_rejects_invalid_values()
    {
        var client = factory.CreateClient();
        var registered = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(registered.AccessToken!);

        var badCurrency = await client.PatchAsJsonAsync("/api/me", new UpdateMeRequest(BaseCurrency: "ZARR"));
        badCurrency.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await badCurrency.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code.ShouldBe("invalid_base_currency");

        var badTimezone = await client.PatchAsJsonAsync("/api/me", new UpdateMeRequest(Timezone: "Not/AZone"));
        badTimezone.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await badTimezone.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code.ShouldBe("invalid_timezone");

        var badSettings = await client.PatchAsJsonAsync("/api/me", new UpdateMeRequest(SettingsJson: "{not json"));
        badSettings.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await badSettings.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code.ShouldBe("invalid_settings_json");
    }
}
