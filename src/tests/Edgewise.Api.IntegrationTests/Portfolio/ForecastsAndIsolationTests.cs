using System.Net;
using System.Net.Http.Json;
using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Portfolio;

[Collection(ApiCollection.Name)]
public sealed class ForecastsAndIsolationTests(TestAppFactory factory)
{
    private static ForecastAssumptionsDto SampleAssumptions() => new(
        [
            new ForecastAssetAssumptionDto("etf", 1_000_000, 0.10m, 0.15m),
            new ForecastAssetAssumptionDto("btc", 250_000, 0.20m, 0.60m),
        ],
        new ForecastContributionDto(120_000, 0.05m, new Dictionary<string, decimal> { ["etf"] = 0.8m, ["btc"] = 0.2m }),
        Reinvest: true,
        HorizonYears: 5,
        HaircutPct: 0.20m);

    [Fact]
    public async Task Forecast_crud_run_and_persisted_bands()
    {
        var (client, userId) = await factory.NewUserClientAsync();

        var created = await client.PostAsJsonAsync(
            "/api/forecasts", new SaveForecastRequest("Base case", SampleAssumptions()), PortfolioTestHelpers.Json);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var forecast = (await created.Content.ReadFromJsonAsync<ForecastDto>(PortfolioTestHelpers.Json))!;
        forecast.Result.ShouldBeNull();
        forecast.Assumptions.HaircutPct.ShouldBe(0.20m);

        var run = await client.PostAsync($"/api/forecasts/{forecast.Id}/run?monteCarlo=true&paths=500&seed=42", null);
        run.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await run.Content.ReadFromJsonAsync<ForecastResultDto>(PortfolioTestHelpers.Json))!;

        result.Deterministic.Count.ShouldBe(5);
        result.Deterministic[0].BearMinor.ShouldBeLessThan(result.Deterministic[0].BullMinor);
        result.Deterministic[4].BaseMinor.ShouldBeGreaterThan(1_250_000); // growth + contributions
        result.MonteCarloBands.ShouldNotBeNull();
        result.MonteCarloBands!.Count.ShouldBe(5);
        var lastBand = result.MonteCarloBands[4];
        lastBand.P5Minor.ShouldBeLessThan(lastBand.P50Minor);
        lastBand.P50Minor.ShouldBeLessThan(lastBand.P95Minor);

        // Result persisted on the entity and returned on GET.
        var fetched = await client.GetFromJsonAsync<ForecastDto>(
            $"/api/forecasts/{forecast.Id}", PortfolioTestHelpers.Json);
        fetched!.Result.ShouldNotBeNull();
        fetched.Result!.Deterministic.Count.ShouldBe(5);

        await using (var db = factory.CreateDbContext(userId))
        {
            var entity = await db.Forecasts.SingleAsync(f => f.Id == forecast.Id);
            entity.ResultJson.ShouldNotBeNullOrWhiteSpace();
        }

        // Updating assumptions clears the stale result.
        var patched = await client.PatchAsJsonAsync(
            $"/api/forecasts/{forecast.Id}",
            new SaveForecastRequest("Base case v2", SampleAssumptions() with { HorizonYears = 3 }),
            PortfolioTestHelpers.Json);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await patched.Content.ReadFromJsonAsync<ForecastDto>(PortfolioTestHelpers.Json))!.Result.ShouldBeNull();

        (await client.DeleteAsync($"/api/forecasts/{forecast.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Forecast_validation_rejects_out_of_range_assumptions()
    {
        var (client, _) = await factory.NewUserClientAsync();

        var badHorizon = await client.PostAsJsonAsync(
            "/api/forecasts",
            new SaveForecastRequest("Bad", SampleAssumptions() with { HorizonYears = 25 }),
            PortfolioTestHelpers.Json);
        badHorizon.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var badGrowth = SampleAssumptions();
        badGrowth.Assets[0] = badGrowth.Assets[0] with { AnnualGrowthPct = 5m }; // 500%
        var badGrowthResponse = await client.PostAsJsonAsync(
            "/api/forecasts", new SaveForecastRequest("Bad", badGrowth), PortfolioTestHelpers.Json);
        badGrowthResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Seed_from_portfolio_builds_capped_assumptions()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var longTerm = buckets.Single(b => b.Kind == BucketKind.LongTerm);

        // Bought at 500, now 2000 after 1 year => raw CAGR 300%, capped to 25%.
        var instrument = await factory.SeedInstrumentAsync(currency: "ZAR", quotePrice: 2000m);
        var holding = await client.CreateHoldingAsync(longTerm.Id, instrument.Id);
        await client.CreateLotAsync(holding.Id, 1m, 50000, DateTime.UtcNow.AddYears(-1));

        var seed = await client.GetFromJsonAsync<ForecastSeedDto>("/api/forecasts/seed", PortfolioTestHelpers.Json);
        var asset = seed!.Assumptions.Assets.Single();
        asset.CurrentValueMinor.ShouldBe(200000);
        asset.AnnualGrowthPct.ShouldBe(0.25m);
        seed.Notes.Single().GrowthCapped.ShouldBeTrue();
        seed.Assumptions.HaircutPct.ShouldBe(0.20m);
        seed.Assumptions.Contribution.Splits.ContainsKey(asset.Key).ShouldBeTrue();
    }

    [Fact]
    public async Task Portfolio_data_is_isolated_between_users()
    {
        var (clientA, _) = await factory.NewUserClientAsync();
        var bucketsA = await clientA.GetBucketsAsync();
        var longTermA = bucketsA.Single(b => b.Kind == BucketKind.LongTerm);

        var instrument = await factory.SeedInstrumentAsync(currency: "ZAR", quotePrice: 100m);
        var holdingA = await clientA.CreateHoldingAsync(longTermA.Id, instrument.Id);
        await clientA.CreateLotAsync(holdingA.Id, 1m, 5000, DateTime.UtcNow.AddDays(-30));

        var (clientB, _) = await factory.NewUserClientAsync();
        (await clientB.GetHoldingsAsync()).ShouldBeEmpty();

        var summaryB = await clientB.GetFromJsonAsync<PortfolioSummaryDto>(
            "/api/portfolio/summary", PortfolioTestHelpers.Json);
        summaryB!.TotalValueMinor.ShouldBe(0);
        summaryB.PerBucket.Count.ShouldBe(4); // B's own seeded buckets, all empty

        // B cannot address A's holding directly.
        (await clientB.GetAsync($"/api/holdings/{holdingA.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var forecastsB = await clientB.GetFromJsonAsync<List<ForecastDto>>(
            "/api/forecasts", PortfolioTestHelpers.Json);
        forecastsB.ShouldNotBeNull();
        forecastsB.ShouldBeEmpty();
    }
}
