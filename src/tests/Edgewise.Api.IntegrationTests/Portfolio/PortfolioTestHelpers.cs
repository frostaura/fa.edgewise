using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;
using Edgewise.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Portfolio;

/// <summary>Shared plumbing for the portfolio vertical's integration tests.</summary>
public static class PortfolioTestHelpers
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;
    }

    public static EdgewiseDbContext CreateDbContext(this TestAppFactory factory, Guid? userId = null)
    {
        var options = new DbContextOptionsBuilder<EdgewiseDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .Options;
        return new EdgewiseDbContext(options, new StubCurrentUser(userId));
    }

    /// <summary>Registers a fresh user and returns an authenticated client plus ids.</summary>
    public static async Task<(HttpClient Client, Guid UserId)> NewUserClientAsync(this TestAppFactory factory)
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        return (client, auth.User!.Id);
    }

    public static async Task<List<BucketDto>> GetBucketsAsync(this HttpClient client)
    {
        var buckets = await client.GetFromJsonAsync<List<BucketDto>>("/api/buckets", Json);
        buckets.ShouldNotBeNull();
        return buckets;
    }

    public static async Task<Instrument> SeedInstrumentAsync(
        this TestAppFactory factory,
        string currency = "ZAR",
        AssetClass assetClass = AssetClass.Equity,
        decimal? quotePrice = null,
        DateTime? quoteAsOf = null)
    {
        await using var db = factory.CreateDbContext();
        var instrument = new Instrument
        {
            Id = Guid.NewGuid(),
            Symbol = $"TST-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Test Instrument",
            AssetClass = assetClass,
            Exchange = "TEST",
            Currency = currency,
        };
        db.Instruments.Add(instrument);
        if (quotePrice is { } price)
        {
            db.QuoteCaches.Add(new QuoteCache
            {
                InstrumentId = instrument.Id,
                Price = price,
                AsOf = quoteAsOf ?? DateTime.UtcNow,
                Provider = "test",
            });
        }

        await db.SaveChangesAsync();
        return instrument;
    }

    public static async Task SetQuoteAsync(
        this TestAppFactory factory, Guid instrumentId, decimal price, DateTime? asOf = null)
    {
        await using var db = factory.CreateDbContext();
        var quote = await db.QuoteCaches.FirstOrDefaultAsync(q => q.InstrumentId == instrumentId);
        if (quote is null)
        {
            db.QuoteCaches.Add(new QuoteCache
            {
                InstrumentId = instrumentId,
                Price = price,
                AsOf = asOf ?? DateTime.UtcNow,
                Provider = "test",
            });
        }
        else
        {
            quote.Price = price;
            quote.AsOf = asOf ?? DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public static async Task SeedFxRateAsync(
        this TestAppFactory factory, string from, string to, decimal rate, DateOnly? date = null)
    {
        await using var db = factory.CreateDbContext();
        db.FxRates.Add(new FxRate
        {
            Base = from,
            Quote = to,
            Date = date ?? DateOnly.FromDateTime(DateTime.UtcNow),
            Rate = rate,
            Provider = "test",
        });
        await db.SaveChangesAsync();
    }

    public static async Task<HoldingDto> CreateHoldingAsync(
        this HttpClient client, Guid bucketId, Guid instrumentId, decimal? manualGrowthRatePct = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/holdings",
            new CreateHoldingRequest(bucketId, instrumentId, null, manualGrowthRatePct),
            Json);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<HoldingDto>(Json))!;
    }

    public static async Task<LotDto> CreateLotAsync(
        this HttpClient client, Guid holdingId, decimal qty, long costMinor, DateTime acquiredAt, string? currency = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/holdings/{holdingId}/lots",
            new LotRequest(qty, costMinor, currency, acquiredAt, "test"),
            Json);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<LotDto>(Json))!;
    }

    public static async Task<List<HoldingDto>> GetHoldingsAsync(this HttpClient client)
    {
        var holdings = await client.GetFromJsonAsync<List<HoldingDto>>("/api/holdings", Json);
        holdings.ShouldNotBeNull();
        return holdings;
    }
}
