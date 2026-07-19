using System.Net;
using System.Net.Http.Json;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Jobs.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Cockpit;

[Collection(ApiCollection.Name)]
public sealed class AlertEvaluationTests(TestAppFactory factory)
{
    private async Task RunJobAsync()
    {
        using var scope = factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<AlertEvaluationJob>();
        await job.RunAsync(CancellationToken.None);
    }

    private async Task SeedQuoteAsync(Guid instrumentId, decimal price)
    {
        await using var db = factory.CreateDbContext(null);
        var existing = await db.QuoteCaches.FindAsync(instrumentId);
        if (existing is null)
        {
            db.QuoteCaches.Add(new QuoteCache
            {
                InstrumentId = instrumentId,
                Price = price,
                AsOf = DateTime.UtcNow,
                Provider = "test",
            });
        }
        else
        {
            existing.Price = price;
            existing.AsOf = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PriceCross_alert_triggers_notification_and_respects_cooldown()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        Guid instrumentId;
        await using (var db = factory.CreateDbContext(null))
        {
            instrumentId = db.Instruments.First(i => i.Symbol == "BTC-USD").Id;
        }

        await SeedQuoteAsync(instrumentId, 60_000m);

        var created = await client.PostAsJsonAsync("/api/alerts", new
        {
            kind = "priceCross",
            instrumentId,
            @params = new { level = 50_000, direction = "above" },
            cooldownMinutes = 60,
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var alert = await created.ReadJsonAsync();
        var alertId = alert.GetProperty("id").GetGuid();

        await RunJobAsync();

        var notifications = await (await client.GetAsync("/api/notifications?unread=true")).ReadJsonAsync();
        var mine = notifications.EnumerateArray()
            .Where(n => n.TryGetProperty("alertId", out var a) && a.GetGuid() == alertId)
            .ToList();
        mine.Count.ShouldBe(1);
        mine[0].GetProperty("title").GetString()!.ShouldContain("BTC-USD");
        mine[0].GetProperty("deepLink").GetString()!.ShouldContain(instrumentId.ToString());
        mine[0].GetProperty("channels").GetString()!.ShouldContain("inapp");

        // Cooldown: a second run within 60 minutes must not re-trigger.
        await RunJobAsync();
        var again = await (await client.GetAsync("/api/notifications")).ReadJsonAsync();
        again.EnumerateArray()
            .Count(n => n.TryGetProperty("alertId", out var a) && a.GetGuid() == alertId)
            .ShouldBe(1);

        // lastTriggeredAt is now surfaced on the alert.
        var alerts = await (await client.GetAsync("/api/alerts")).ReadJsonAsync();
        alerts.EnumerateArray()
            .Single(a => a.GetProperty("id").GetGuid() == alertId)
            .TryGetProperty("lastTriggeredAt", out _)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task PriceCross_below_does_not_trigger_when_price_is_above()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        Guid instrumentId;
        await using (var db = factory.CreateDbContext(null))
        {
            instrumentId = db.Instruments.First(i => i.Symbol == "ETH-USD").Id;
        }

        await SeedQuoteAsync(instrumentId, 3_000m);

        var created = await client.PostAsJsonAsync("/api/alerts", new
        {
            kind = "priceCross",
            instrumentId,
            @params = new { level = 1_000, direction = "below" },
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var alertId = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();

        await RunJobAsync();

        var notifications = await (await client.GetAsync("/api/notifications")).ReadJsonAsync();
        notifications.EnumerateArray()
            .Count(n => n.TryGetProperty("alertId", out var a) && a.GetGuid() == alertId)
            .ShouldBe(0);
    }

    [Fact]
    public async Task FgExtreme_alert_triggers_on_extreme_sentiment()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        await using (var db = factory.CreateDbContext(null))
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (await db.SentimentReadings.FindAsync(SentimentKind.CryptoFG, today) is { } existing)
            {
                existing.Value = 10;
                existing.Label = "Extreme Fear";
            }
            else
            {
                db.SentimentReadings.Add(new SentimentReading
                {
                    Kind = SentimentKind.CryptoFG,
                    Date = today,
                    Value = 10,
                    Label = "Extreme Fear",
                });
            }

            await db.SaveChangesAsync();
        }

        var created = await client.PostAsJsonAsync("/api/alerts", new
        {
            kind = "fgExtreme",
            @params = new { min = 20, max = 80 },
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var alertId = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();

        await RunJobAsync();

        var notifications = await (await client.GetAsync("/api/notifications")).ReadJsonAsync();
        var mine = notifications.EnumerateArray()
            .Where(n => n.TryGetProperty("alertId", out var a) && a.GetGuid() == alertId)
            .ToList();
        mine.Count.ShouldBe(1);
        mine[0].GetProperty("title").GetString()!.ShouldContain("10");
    }

    [Fact]
    public async Task Alert_validation_rejects_bad_params()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        // Unknown kind
        (await client.PostAsJsonAsync("/api/alerts", new { kind = "nope" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // priceCross without instrument
        (await client.PostAsJsonAsync("/api/alerts", new
        {
            kind = "priceCross",
            @params = new { level = 100, direction = "above" },
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // fgExtreme with min >= max
        (await client.PostAsJsonAsync("/api/alerts", new
        {
            kind = "fgExtreme",
            @params = new { min = 90, max = 10 },
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
