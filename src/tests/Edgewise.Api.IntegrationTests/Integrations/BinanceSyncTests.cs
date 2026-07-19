using System.Net.Http.Json;
using Edgewise.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Integrations;

[Collection(ApiCollection.Name)]
public sealed class BinanceSyncTests(TestAppFactory factory) : IDisposable
{
    private const string ApiKey = "AbCdEfGh1234567890TestBinanceKeyXYZ9876";
    private const string ApiSecret = "SuperSecretHmacSigningValueDoNotLog0001";

    private readonly IntegrationsHarness _harness = new(factory);

    public void Dispose() => _harness.Dispose();

    private object ConnectRequest(Guid bucketId) => new
    {
        venue = "binance",
        bucketId,
        name = "Binance spot",
        apiKey = ApiKey,
        apiSecret = ApiSecret,
    };

    [Fact]
    public async Task Connect_rejects_key_with_trading_enabled()
    {
        _harness.Http.WhenJson("/sapi/v1/account/apiRestrictions", IntegrationFixtures.BinanceRestrictionsTradingEnabled);
        var (client, userId, bucketId) = await _harness.NewUserAsync();

        var response = await client.PostAsJsonAsync(
            "/api/accounts/connect", ConnectRequest(bucketId), IntegrationsHarness.Json);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("key_over_scoped");
        body.ShouldContain("Spot");
        body.ShouldNotContain(ApiKey);
        body.ShouldNotContain(ApiSecret);

        // Over-scoped key → no account stored at all.
        var count = await _harness.WithDbAsync(db =>
            db.Accounts.IgnoreQueryFilters().CountAsync(a => a.UserId == userId));
        count.ShouldBe(0);
    }

    [Fact]
    public async Task Connect_rejects_key_without_read_permission()
    {
        _harness.Http.WhenJson("/sapi/v1/account/apiRestrictions", IntegrationFixtures.BinanceRestrictionsNoReading);
        var (client, _, bucketId) = await _harness.NewUserAsync();

        var response = await client.PostAsJsonAsync(
            "/api/accounts/connect", ConnectRequest(bucketId), IntegrationsHarness.Json);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Reading");
    }

    [Fact]
    public async Task Connect_accepts_read_only_key_and_exposes_only_last_four()
    {
        _harness.Http.WhenJson("/sapi/v1/account/apiRestrictions", IntegrationFixtures.BinanceRestrictionsReadOnly);
        var (client, _, bucketId) = await _harness.NewUserAsync();

        var connected = await _harness.ConnectAsync(client, ConnectRequest(bucketId));

        connected.Account.Status.ShouldBe(AccountStatus.Connected);
        connected.Account.KeyLastFour.ShouldBe(ApiKey[^4..]);

        // The restriction check must be signed and carry the API key header, not log-style params.
        var restrictionCall = _harness.Http.Requests.Single(r => r.Contains("apiRestrictions"));
        restrictionCall.ShouldContain("signature=");
        restrictionCall.ShouldContain("timestamp=");

        // The whole list payload never leaks credentials.
        var raw = await client.GetStringAsync("/api/accounts/");
        raw.ShouldNotContain(ApiKey);
        raw.ShouldNotContain(ApiSecret);
        raw.ShouldContain(ApiKey[^4..]);
    }

    [Fact]
    public async Task Sync_discovers_symbols_maps_trades_and_resumes_with_fromId()
    {
        _harness.Http.WhenJson("/sapi/v1/account/apiRestrictions", IntegrationFixtures.BinanceRestrictionsReadOnly);
        _harness.Http.WhenJson("/api/v3/account", IntegrationFixtures.BinanceAccount);
        _harness.Http.WhenJson("/api/v3/exchangeInfo", IntegrationFixtures.BinanceExchangeInfo);
        _harness.Http.When("/api/v3/myTrades", request =>
        {
            var symbol = FakeIntegrationHttpFactory.Query(request, "symbol");
            var fromId = FakeIntegrationHttpFactory.Query(request, "fromId");
            return symbol == "BTCUSDT" && fromId is null
                ? FakeIntegrationHttpFactory.JsonResponse(IntegrationFixtures.BinanceMyTradesBtcUsdt)
                : FakeIntegrationHttpFactory.JsonResponse(IntegrationFixtures.EmptyArray);
        });
        var (client, userId, bucketId) = await _harness.NewUserAsync();

        var connected = await _harness.ConnectAsync(client, ConnectRequest(bucketId));
        var accountId = connected.Account.Id;

        var sync = await _harness.SyncAsync(client, accountId);
        var stats = sync.Account!.LastSyncStats!;
        stats.LastRunFills.ShouldBe(2);
        // Balances BTC+USDT → candidates BTCUSDT & BTCUSDC survive exchangeInfo (ETH balance is zero).
        stats.SymbolsTotal.ShouldBe(2);
        stats.SymbolsDone.ShouldBe(2);
        stats.LastTradeIdPerSymbol!["BTCUSDT"].ShouldBe(28460);

        var fills = await _harness.WithDbAsync(db => db.Fills.IgnoreQueryFilters()
            .Where(f => f.AccountId == accountId)
            .OrderBy(f => f.At)
            .ToListAsync());
        fills.Count.ShouldBe(2);
        fills.All(f => f.UserId == userId && f.MatchStatus == MatchStatus.Proposed).ShouldBeTrue();

        fills[0].Side.ShouldBe(FillSide.Buy);
        fills[0].Qty.ShouldBe(0.005m);
        fills[0].Price.ShouldBe(60000m);
        fills[0].FeeMinor.ShouldBe(30); // 0.30 USDT → 30 US cents
        fills[0].FeeCurrency.ShouldBe("USD");

        fills[1].Side.ShouldBe(FillSide.Sell);
        fills[1].FeeMinor.ShouldBe(0); // BNB commission: best-effort 0 + note in raw payload
        fills[1].FeeCurrency.ShouldBe("BNB");
        fills[1].RawPayloadJson!.ShouldContain("feeNote");

        // Second sync resumes from lastTradeId + 1 and imports nothing new.
        _harness.Http.Requests.Clear();
        var second = await _harness.SyncAsync(client, accountId);
        second.Account!.LastSyncStats!.LastRunFills.ShouldBe(0);
        _harness.Http.Requests
            .Any(r => r.Contains("myTrades") && r.Contains("symbol=BTCUSDT") && r.Contains("fromId=28461"))
            .ShouldBeTrue();
        (await _harness.WithDbAsync(db => db.Fills.IgnoreQueryFilters()
            .CountAsync(f => f.AccountId == accountId))).ShouldBe(2);
    }
}
