using System.Net.Http.Json;
using Edgewise.Api.Services.Integrations;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Ingestion.Connectors;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Integrations;

[Collection(ApiCollection.Name)]
public sealed class PolymarketSyncTests(TestAppFactory factory) : IDisposable
{
    private readonly IntegrationsHarness _harness = new(factory);

    public void Dispose() => _harness.Dispose();

    private void RouteTradesWithOffsetPaging()
    {
        _harness.Http.When("/trades", request =>
            FakeIntegrationHttpFactory.Query(request, "offset") is "0" or null
                ? FakeIntegrationHttpFactory.JsonResponse(IntegrationFixtures.PolymarketTrades)
                : FakeIntegrationHttpFactory.JsonResponse(IntegrationFixtures.EmptyArray));
    }

    [Fact]
    public async Task Connect_with_activity_returns_no_warning_and_masks_wallet()
    {
        _harness.Http.WhenJson("/positions", IntegrationFixtures.PolymarketPositionsOpen);
        var (client, _, bucketId) = await _harness.NewUserAsync();

        var connected = await _harness.ConnectAsync(client, new
        {
            venue = "polymarket",
            bucketId,
            name = "My Polymarket",
            walletAddress = IntegrationFixtures.Wallet,
        });

        connected.Warning.ShouldBeNull();
        connected.Account.Venue.ShouldBe(Venue.Polymarket);
        connected.Account.WalletAddress.ShouldNotBeNull();
        connected.Account.WalletAddress.ShouldNotBe(IntegrationFixtures.Wallet);
        connected.Account.WalletAddress.ShouldStartWith(IntegrationFixtures.Wallet[..8]);
        connected.Account.WalletAddress.ShouldEndWith(IntegrationFixtures.Wallet[^4..]);
    }

    [Fact]
    public async Task Connect_with_empty_wallet_warns_about_proxy_wallet_but_still_connects()
    {
        _harness.Http.WhenJson("/positions", IntegrationFixtures.EmptyArray);
        _harness.Http.WhenJson("/activity", IntegrationFixtures.EmptyArray);
        var (client, _, bucketId) = await _harness.NewUserAsync();

        var connected = await _harness.ConnectAsync(client, new
        {
            venue = "polymarket",
            bucketId,
            walletAddress = IntegrationFixtures.Wallet,
        });

        connected.Warning.ShouldNotBeNull();
        connected.Warning.ShouldContain("proxy");
        connected.Account.Status.ShouldBe(AccountStatus.Connected);
        connected.Account.LastSyncStats!.Warning.ShouldNotBeNull();
    }

    [Fact]
    public async Task Connect_rejects_malformed_wallet()
    {
        var (client, _, bucketId) = await _harness.NewUserAsync();

        var response = await client.PostAsJsonAsync("/api/accounts/connect", new
        {
            venue = "polymarket",
            bucketId,
            walletAddress = "not-a-wallet",
        }, IntegrationsHarness.Json);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_wallet");
    }

    [Fact]
    public async Task Sync_maps_trades_to_proposed_fills_and_is_idempotent()
    {
        RouteTradesWithOffsetPaging();
        _harness.Http.WhenJson("/positions", IntegrationFixtures.PolymarketPositionsOpen);
        _harness.Http.WhenJson("/activity", IntegrationFixtures.PolymarketPositionsOpen);
        var (client, userId, bucketId) = await _harness.NewUserAsync();

        var connected = await _harness.ConnectAsync(client, new
        {
            venue = "polymarket",
            bucketId,
            walletAddress = IntegrationFixtures.Wallet,
        });
        var accountId = connected.Account.Id;

        var sync = await _harness.SyncAsync(client, accountId);
        sync.Queued.ShouldBeFalse();
        sync.Account!.Status.ShouldBe(AccountStatus.Connected);
        sync.Account.LastSyncStats!.LastRunFills.ShouldBe(3);

        var fills = await _harness.WithDbAsync(db => db.Fills.IgnoreQueryFilters()
            .Where(f => f.AccountId == accountId)
            .OrderBy(f => f.At)
            .ToListAsync());
        fills.Count.ShouldBe(3);
        fills.All(f => f.UserId == userId).ShouldBeTrue();
        fills.All(f => f.MatchStatus == MatchStatus.Proposed).ShouldBeTrue();
        fills.All(f => f.Source == FillSource.Api).ShouldBeTrue();
        fills[0].Side.ShouldBe(FillSide.Buy);
        fills[0].Qty.ShouldBe(100m);
        fills[0].Price.ShouldBe(0.62m); // probability stored as-is
        fills[2].Side.ShouldBe(FillSide.Sell);
        fills[2].Price.ShouldBe(0.31m);

        var instrument = await _harness.WithDbAsync(db =>
            db.Instruments.FirstAsync(i => i.Id == fills[0].InstrumentId));
        instrument.Exchange.ShouldBe("Polymarket");
        instrument.AssetClass.ShouldBe(AssetClass.Prediction);
        instrument.Symbol.ShouldBe("will-btc-close-above-120k-in-2026");
        instrument.ProviderSymbolsJson!.ShouldContain(IntegrationFixtures.ConditionId);

        // Re-run: same trades served again at offset 0? No — sync resumes past the
        // stored offset, and SourceHash dedupe guards any overlap.
        var second = await _harness.SyncAsync(client, accountId);
        second.Account!.LastSyncStats!.LastRunFills.ShouldBe(0);
        var count = await _harness.WithDbAsync(db => db.Fills.IgnoreQueryFilters()
            .CountAsync(f => f.AccountId == accountId));
        count.ShouldBe(3);
    }

    [Fact]
    public async Task Sync_creates_brier_forecasts_and_resolves_redeemable_positions()
    {
        RouteTradesWithOffsetPaging();
        _harness.Http.WhenJson("/positions", IntegrationFixtures.PolymarketPositionsResolved);
        var (client, userId, bucketId) = await _harness.NewUserAsync();

        var connected = await _harness.ConnectAsync(client, new
        {
            venue = "polymarket",
            bucketId,
            walletAddress = IntegrationFixtures.Wallet,
        });
        await _harness.SyncAsync(client, connected.Account.Id);

        var forecasts = await _harness.WithDbAsync(db => db.BrierForecasts.IgnoreQueryFilters()
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.Question)
            .ToListAsync());

        // One forecast per (market, traded outcome): BTC/Yes and Fed/No.
        forecasts.Count.ShouldBe(2);

        var btc = forecasts.Single(f => f.Question.Contains("BTC"));
        btc.PMarket.ShouldBe(0.62m); // entry price of the first trade
        btc.Outcome.ShouldBe(true); // redeemable Yes position at curPrice 1.0
        btc.BrierScore.ShouldBe((0.62m - 1m) * (0.62m - 1m));
        btc.ResolvedAt.ShouldNotBeNull();

        var fed = forecasts.Single(f => f.Question.Contains("Fed"));
        fed.PUser.ShouldBe(fed.PMarket); // pUser defaults to market price until edited
        fed.Outcome.ShouldBeNull(); // no resolved position for that market
    }
}
