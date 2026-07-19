using System.Net;
using System.Net.Http.Json;
using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Portfolio;

/// <summary>
/// Holdings valuation through the QuoteCache/FxRate fallback path (no live
/// MarketDataService registered): quoted, FX-converted, stale and manual assets.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HoldingsValuationTests(TestAppFactory factory)
{
    [Fact]
    public async Task Quoted_holding_is_valued_from_quote_cache()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var longTerm = buckets.Single(b => b.Kind == BucketKind.LongTerm);

        var instrument = await factory.SeedInstrumentAsync(currency: "ZAR", quotePrice: 150m);
        var holding = await client.CreateHoldingAsync(longTerm.Id, instrument.Id);
        await client.CreateLotAsync(holding.Id, qty: 2m, costMinor: 20000, DateTime.UtcNow.AddMonths(-2));

        var holdings = await client.GetHoldingsAsync();
        var valued = holdings.Single(h => h.Id == holding.Id);

        valued.Qty.ShouldBe(2m);
        valued.CostBasisMinor.ShouldBe(20000);
        valued.ValueMinor.ShouldBe(30000); // 2 x R150.00 = R300.00
        valued.UnrealisedPnlMinor.ShouldBe(10000);
        valued.UnrealisedPnlPct.ShouldNotBeNull();
        valued.UnrealisedPnlPct!.Value.ShouldBe(0.5m, 0.0001m);
        valued.Stale.ShouldBeFalse();
        valued.BucketWeightPct.ShouldBe(1m, 0.0001m);
        valued.TotalWeightPct.ShouldBe(1m, 0.0001m);
    }

    [Fact]
    public async Task Foreign_currency_holding_converts_via_fx_rate()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var trading = buckets.Single(b => b.Kind == BucketKind.Trading);

        var instrument = await factory.SeedInstrumentAsync(currency: "USD", assetClass: AssetClass.Crypto, quotePrice: 100m);
        await factory.SeedFxRateAsync("USD", "ZAR", 18m);

        var holding = await client.CreateHoldingAsync(trading.Id, instrument.Id);
        await client.CreateLotAsync(holding.Id, qty: 0.5m, costMinor: 80000, DateTime.UtcNow.AddMonths(-1), currency: "ZAR");

        var valued = (await client.GetHoldingsAsync()).Single(h => h.Id == holding.Id);
        valued.ValueMinor.ShouldBe(90000); // 0.5 x $100 x 18 ZAR/USD x 100c
        valued.CostBasisMinor.ShouldBe(80000);
        valued.Stale.ShouldBeFalse();
    }

    [Fact]
    public async Task Old_quote_is_flagged_stale_and_missing_quote_falls_back_to_cost()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var longTerm = buckets.Single(b => b.Kind == BucketKind.LongTerm);

        var staleInstrument = await factory.SeedInstrumentAsync(
            currency: "ZAR", quotePrice: 100m, quoteAsOf: DateTime.UtcNow.AddDays(-3));
        var staleHolding = await client.CreateHoldingAsync(longTerm.Id, staleInstrument.Id);
        await client.CreateLotAsync(staleHolding.Id, qty: 1m, costMinor: 5000, DateTime.UtcNow.AddMonths(-1));

        var noQuoteInstrument = await factory.SeedInstrumentAsync(currency: "ZAR", quotePrice: null);
        var noQuoteHolding = await client.CreateHoldingAsync(longTerm.Id, noQuoteInstrument.Id);
        await client.CreateLotAsync(noQuoteHolding.Id, qty: 3m, costMinor: 12000, DateTime.UtcNow.AddMonths(-1));

        var holdings = await client.GetHoldingsAsync();
        holdings.Single(h => h.Id == staleHolding.Id).Stale.ShouldBeTrue();
        holdings.Single(h => h.Id == staleHolding.Id).ValueMinor.ShouldBe(10000);

        var fallback = holdings.Single(h => h.Id == noQuoteHolding.Id);
        fallback.Stale.ShouldBeTrue();
        fallback.ValueMinor.ShouldBe(12000); // last-known cost basis
    }

    [Fact]
    public async Task Manual_asset_grows_at_declared_rate_from_acquisition()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var cash = buckets.Single(b => b.Kind == BucketKind.Cash);

        var instrument = await factory.SeedInstrumentAsync(currency: "ZAR", assetClass: AssetClass.Custom);
        var holding = await client.CreateHoldingAsync(cash.Id, instrument.Id, manualGrowthRatePct: 0.10m);
        holding.ManualGrowthRatePct.ShouldBe(0.10m);

        await client.CreateLotAsync(holding.Id, qty: 1m, costMinor: 100000, DateTime.UtcNow.AddDays(-365.25 / 2));

        var valued = (await client.GetHoldingsAsync()).Single(h => h.Id == holding.Id);
        // Half a year at 10% APR: 100000 x 1.1^0.5 ~= 104881.
        valued.ValueMinor.ShouldBeInRange(104600, 105200);
        valued.Stale.ShouldBeFalse();
    }

    [Fact]
    public async Task Thesis_patch_and_holding_detail_with_lots()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var longTerm = buckets.Single(b => b.Kind == BucketKind.LongTerm);

        var instrument = await factory.SeedInstrumentAsync(quotePrice: 10m);
        var holding = await client.CreateHoldingAsync(longTerm.Id, instrument.Id);
        await client.CreateLotAsync(holding.Id, 1m, 1000, DateTime.UtcNow.AddDays(-10));
        await client.CreateLotAsync(holding.Id, 2m, 2100, DateTime.UtcNow.AddDays(-5));

        var patch = await client.PatchAsJsonAsync(
            $"/api/holdings/{holding.Id}/thesis",
            new UpdateThesisRequest("## Why\nCompounding."),
            PortfolioTestHelpers.Json);
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await client.GetFromJsonAsync<HoldingDto>(
            $"/api/holdings/{holding.Id}", PortfolioTestHelpers.Json);
        detail!.ThesisNotesMd.ShouldBe("## Why\nCompounding.");
        detail.Lots.ShouldNotBeNull();
        detail.Lots!.Count.ShouldBe(2);
        detail.Qty.ShouldBe(3m);
        detail.CostBasisMinor.ShouldBe(3100);
    }
}
