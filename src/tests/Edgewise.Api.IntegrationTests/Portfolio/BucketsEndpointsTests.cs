using System.Net;
using System.Net.Http.Json;
using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Portfolio;

[Collection(ApiCollection.Name)]
public sealed class BucketsEndpointsTests(TestAppFactory factory)
{
    [Fact]
    public async Task Seeded_buckets_are_listed_with_fraction_percentages()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();

        buckets.Count.ShouldBe(4);
        buckets.Select(b => b.Kind).ShouldBe(
            [BucketKind.LongTerm, BucketKind.Trading, BucketKind.Prediction, BucketKind.Cash],
            ignoreOrder: true);
        buckets.Single(b => b.Kind == BucketKind.LongTerm).TargetAllocPct.ShouldBe(0.60m);
        buckets.Single(b => b.Kind == BucketKind.Cash).ContributionSplitPct.ShouldBe(0.10m);
    }

    [Fact]
    public async Task Create_patch_and_delete_bucket()
    {
        var (client, _) = await factory.NewUserClientAsync();

        var created = await client.PostAsJsonAsync(
            "/api/buckets",
            new CreateBucketRequest("Side bets", BucketKind.Trading, 0.05m, 0.05m, null),
            PortfolioTestHelpers.Json);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var bucket = (await created.Content.ReadFromJsonAsync<BucketDto>(PortfolioTestHelpers.Json))!;
        bucket.TargetAllocPct.ShouldBe(0.05m);
        bucket.Currency.ShouldBe("ZAR");

        var patched = await client.PatchAsJsonAsync(
            $"/api/buckets/{bucket.Id}",
            new UpdateBucketRequest("Side bets 2", 0.08m, null),
            PortfolioTestHelpers.Json);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await patched.Content.ReadFromJsonAsync<BucketDto>(PortfolioTestHelpers.Json))!;
        updated.Name.ShouldBe("Side bets 2");
        updated.TargetAllocPct.ShouldBe(0.08m);
        updated.ContributionSplitPct.ShouldBe(0.05m);

        (await client.DeleteAsync($"/api/buckets/{bucket.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_requires_empty_bucket()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var longTerm = buckets.Single(b => b.Kind == BucketKind.LongTerm);

        var instrument = await factory.SeedInstrumentAsync(quotePrice: 100m);
        var holding = await client.CreateHoldingAsync(longTerm.Id, instrument.Id);

        var blocked = await client.DeleteAsync($"/api/buckets/{longTerm.Id}");
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await client.DeleteAsync($"/api/holdings/{holding.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/buckets/{longTerm.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Fraction_validation_rejects_percent_points()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var response = await client.PostAsJsonAsync(
            "/api/buckets",
            new CreateBucketRequest("Bad", BucketKind.Cash, 60m, 0.1m, null),
            PortfolioTestHelpers.Json);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
