using System.Net;
using System.Net.Http.Json;
using Edgewise.Api.Services.Integrations;
using Edgewise.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Integrations;

[Collection(ApiCollection.Name)]
public sealed class AccountsEndpointsTests(TestAppFactory factory) : IDisposable
{
    private const string ApiKey = "IsolationTestKeyAAAABBBBCCCCDDDD1234";
    private const string ApiSecret = "IsolationTestSecretDoNotLeak987654321";

    private readonly IntegrationsHarness _harness = new(factory);

    public void Dispose() => _harness.Dispose();

    private async Task<(HttpClient Client, Guid UserId, ConnectAccountResponse Account)> ConnectBinanceAsync()
    {
        _harness.Http.WhenJson("/sapi/v1/account/apiRestrictions", IntegrationFixtures.BinanceRestrictionsReadOnly);
        var (client, userId, bucketId) = await _harness.NewUserAsync();
        var connected = await _harness.ConnectAsync(client, new
        {
            venue = "binance",
            bucketId,
            apiKey = ApiKey,
            apiSecret = ApiSecret,
        });
        return (client, userId, connected);
    }

    [Fact]
    public async Task Accounts_are_isolated_between_users()
    {
        var (_, _, connected) = await ConnectBinanceAsync();

        var (otherClient, _, _) = await _harness.NewUserAsync();
        var otherAccounts = await otherClient.GetFromJsonAsync<List<AccountDto>>(
            "/api/accounts/", IntegrationsHarness.Json);
        otherAccounts!.ShouldNotContain(a => a.Id == connected.Account.Id);

        // Foreign account is invisible to sync/revoke/health too.
        (await otherClient.PostAsync($"/api/accounts/{connected.Account.Id}/sync", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherClient.DeleteAsync($"/api/accounts/{connected.Account.Id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherClient.GetAsync($"/api/accounts/{connected.Account.Id}/health"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Connect_rejects_foreign_or_unknown_bucket()
    {
        _harness.Http.WhenJson("/sapi/v1/account/apiRestrictions", IntegrationFixtures.BinanceRestrictionsReadOnly);
        var (client, _, _) = await _harness.NewUserAsync();
        var (_, _, otherBucketId) = await _harness.NewUserAsync();

        var response = await client.PostAsJsonAsync("/api/accounts/connect", new
        {
            venue = "binance",
            bucketId = otherBucketId,
            apiKey = ApiKey,
            apiSecret = ApiSecret,
        }, IntegrationsHarness.Json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("unknown_bucket");
    }

    [Fact]
    public async Task Credentials_are_stored_encrypted_and_never_returned()
    {
        var (client, _, connected) = await ConnectBinanceAsync();

        var stored = await _harness.WithDbAsync(db => db.Accounts.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(a => a.Id == connected.Account.Id));
        stored.CredentialsEnc.ShouldNotBeNull();
        stored.CredentialsEnc.ShouldStartWith("v1:"); // EnvelopeCrypto blob, not plaintext
        stored.CredentialsEnc.ShouldNotContain(ApiKey);
        stored.CredentialsEnc.ShouldNotContain(ApiSecret);

        foreach (var url in new[]
        {
            "/api/accounts/",
            $"/api/accounts/{connected.Account.Id}/health",
        })
        {
            var raw = await client.GetStringAsync(url);
            raw.ShouldNotContain(ApiKey);
            raw.ShouldNotContain(ApiSecret);
            raw.ShouldNotContain("credentialsEnc");
        }
    }

    [Fact]
    public async Task Revoke_clears_credentials_and_blocks_future_sync()
    {
        var (client, _, connected) = await ConnectBinanceAsync();

        var revoke = await client.DeleteAsync($"/api/accounts/{connected.Account.Id}");
        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var stored = await _harness.WithDbAsync(db => db.Accounts.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(a => a.Id == connected.Account.Id));
        stored.Status.ShouldBe(AccountStatus.Revoked);
        stored.CredentialsEnc.ShouldBeNull();

        var accounts = await client.GetFromJsonAsync<List<AccountDto>>("/api/accounts/", IntegrationsHarness.Json);
        var dto = accounts!.Single(a => a.Id == connected.Account.Id);
        dto.Status.ShouldBe(AccountStatus.Revoked);
        dto.KeyLastFour.ShouldBeNull();

        (await client.PostAsync($"/api/accounts/{connected.Account.Id}/sync", null))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unsupported_venue_is_rejected_with_csv_hint()
    {
        var (client, _, bucketId) = await _harness.NewUserAsync();
        var response = await client.PostAsJsonAsync("/api/accounts/connect", new
        {
            venue = "easyequities",
            bucketId,
        }, IntegrationsHarness.Json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("unsupported_venue");
        body.ShouldContain("CSV");
    }
}
