using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Ingestion.Connectors;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Integrations;

public sealed class CoinbaseJwtGeneratorTests
{
    private const string KeyName = "organizations/11111111-2222-3333-4444-555555555555/apiKeys/aaaa-bbbb";

    [Fact]
    public void Generates_ES256_jwt_with_cdp_header_and_claims()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportECPrivateKeyPem();

        var jwt = CoinbaseJwtGenerator.Generate(
            KeyName, pem, "GET", "api.coinbase.com", "/api/v3/brokerage/accounts");

        var parts = jwt.Split('.');
        parts.Length.ShouldBe(3);

        using var header = JsonDocument.Parse(DecodeBase64Url(parts[0]));
        header.RootElement.GetProperty("alg").GetString().ShouldBe("ES256");
        header.RootElement.GetProperty("kid").GetString().ShouldBe(KeyName);
        header.RootElement.GetProperty("typ").GetString().ShouldBe("JWT");
        header.RootElement.GetProperty("nonce").GetString()!.Length.ShouldBe(32);

        using var claims = JsonDocument.Parse(DecodeBase64Url(parts[1]));
        claims.RootElement.GetProperty("sub").GetString().ShouldBe(KeyName);
        claims.RootElement.GetProperty("iss").GetString().ShouldBe("cdp");
        claims.RootElement.GetProperty("uri").GetString()
            .ShouldBe("GET api.coinbase.com/api/v3/brokerage/accounts");
        var nbf = claims.RootElement.GetProperty("nbf").GetInt64();
        claims.RootElement.GetProperty("exp").GetInt64().ShouldBe(nbf + 120);

        // Signature verifies with the key's public half (r||s IEEE P-1363 form).
        var signingInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        key.VerifyData(signingInput, DecodeBase64Url(parts[2]), HashAlgorithmName.SHA256).ShouldBeTrue();
    }

    [Fact]
    public void Rejects_garbage_pem_with_plain_language_error()
    {
        var ex = Should.Throw<IntegrationException>(() =>
            CoinbaseJwtGenerator.ValidatePrivateKey("-----BEGIN EC PRIVATE KEY-----\nnot-a-key\n-----END EC PRIVATE KEY-----"));
        ex.Code.ShouldBe("coinbase_invalid_private_key");
        ex.Message.ShouldContain("private key");
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

[Collection(ApiCollection.Name)]
public sealed class CoinbaseSyncTests(TestAppFactory factory) : IDisposable
{
    private const string KeyName = "organizations/11111111-2222-3333-4444-555555555555/apiKeys/ffff-9876";

    private readonly IntegrationsHarness _harness = new(factory);
    private readonly string _pem = CreatePem();

    private static string CreatePem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportECPrivateKeyPem();
    }

    public void Dispose() => _harness.Dispose();

    private object ConnectRequest(Guid bucketId) => new
    {
        venue = "coinbase",
        bucketId,
        name = "Coinbase",
        keyName = KeyName,
        privateKeyPem = _pem,
    };

    [Fact]
    public async Task Connect_validates_credentials_and_rejects_401()
    {
        _harness.Http.When("/api/v3/brokerage/accounts", _ =>
            FakeIntegrationHttpFactory.JsonResponse("""{"error":"UNAUTHORIZED"}""", System.Net.HttpStatusCode.Unauthorized));
        var (client, _, bucketId) = await _harness.NewUserAsync();

        var response = await client.PostAsJsonAsync(
            "/api/accounts/connect", ConnectRequest(bucketId), IntegrationsHarness.Json);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("coinbase_invalid_credentials");
        body.ShouldNotContain(_pem[30..60]); // never echo key material
    }

    [Fact]
    public async Task Connect_then_sync_maps_fills_and_is_idempotent()
    {
        _harness.Http.WhenJson("/api/v3/brokerage/accounts", IntegrationFixtures.CoinbaseAccounts);
        _harness.Http.WhenJson("/api/v3/brokerage/orders/historical/fills", IntegrationFixtures.CoinbaseFillsPage);
        var (client, userId, bucketId) = await _harness.NewUserAsync();

        var connected = await _harness.ConnectAsync(client, ConnectRequest(bucketId));
        connected.Account.KeyLastFour.ShouldBe(KeyName[^4..]);
        var accountId = connected.Account.Id;

        // Every request carried a Bearer JWT (three dot-separated segments).
        var validateCall = _harness.Http.Requests.Single(r => r.Contains("brokerage/accounts"));
        validateCall.ShouldStartWith("GET");

        var sync = await _harness.SyncAsync(client, accountId);
        sync.Account!.LastSyncStats!.LastRunFills.ShouldBe(2);
        sync.Account.LastSyncStats.CoinbaseLastSequenceTimestamp.ShouldNotBeNull();

        var fills = await _harness.WithDbAsync(db => db.Fills.IgnoreQueryFilters()
            .Where(f => f.AccountId == accountId)
            .OrderBy(f => f.At)
            .ToListAsync());
        fills.Count.ShouldBe(2);
        fills.All(f => f.UserId == userId && f.MatchStatus == MatchStatus.Proposed).ShouldBeTrue();

        fills[0].Side.ShouldBe(FillSide.Buy);
        fills[0].Qty.ShouldBe(0.001m);
        fills[0].Price.ShouldBe(68000m);
        fills[0].FeeMinor.ShouldBe(125); // $1.25 commission → 125 cents
        fills[0].FeeCurrency.ShouldBe("USD");

        fills[1].Side.ShouldBe(FillSide.Sell);

        var instrument = await _harness.WithDbAsync(db =>
            db.Instruments.FirstAsync(i => i.Id == fills[0].InstrumentId));
        instrument.Symbol.ShouldBe("BTC-USD");
        instrument.AssetClass.ShouldBe(AssetClass.Crypto);
        instrument.Currency.ShouldBe("USD");

        // Re-running the sync with the same fills page creates no duplicates.
        var second = await _harness.SyncAsync(client, accountId);
        second.Account!.LastSyncStats!.LastRunFills.ShouldBe(0);
        (await _harness.WithDbAsync(db => db.Fills.IgnoreQueryFilters()
            .CountAsync(f => f.AccountId == accountId))).ShouldBe(2);
    }
}
