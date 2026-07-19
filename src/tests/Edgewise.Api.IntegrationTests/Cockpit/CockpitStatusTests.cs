using System.Net;
using System.Net.Http.Json;
using Edgewise.Domain.Entities;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Cockpit;

[Collection(ApiCollection.Name)]
public sealed class CockpitStatusTests(TestAppFactory factory)
{
    [Fact]
    public async Task Status_has_five_reads_and_unlocks_for_a_fresh_user()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        var response = await client.GetAsync("/api/cockpit/status");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();

        var reads = body.GetProperty("reads");
        foreach (var read in new[] { "heat", "dailyPnl", "ladder", "calendar", "state" })
        {
            reads.GetProperty(read).GetProperty("status").GetString()
                .ShouldBeOneOf("green", "amber", "red");
            reads.GetProperty(read).GetProperty("detail").GetString().ShouldNotBeNullOrEmpty();
        }

        body.GetProperty("allGreen").GetBoolean().ShouldBeTrue();
        body.GetProperty("newPlanUnlocked").GetBoolean().ShouldBeTrue();
        body.GetProperty("today").GetProperty("tradesCount").GetInt32().ShouldBe(0);
        body.TryGetProperty("activeOverride", out _).ShouldBeFalse(); // null omitted
    }

    [Fact]
    public async Task Declaring_tilted_state_turns_the_state_read_amber()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);

        var response = await client.PostAsJsonAsync("/api/cockpit/state", new { state = "tilted" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();

        var state = body.GetProperty("reads").GetProperty("state");
        state.GetProperty("state").GetString().ShouldBe("tilted");
        state.GetProperty("status").GetString().ShouldBe("amber");

        var invalid = await client.PostAsJsonAsync("/api/cockpit/state", new { state = "zen" });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Scripted_losing_day_trips_circuit_breaker_and_override_unlocks()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        // Seed: trading-bucket equity 10 000.00 and three losing trades closed now.
        // Standard risk profile: DailyStopPct 3% (-300.00), DailyLossCountStop 3.
        await using (var db = factory.CreateDbContext(userId))
        {
            var tradingBucket = db.Buckets.Single(b => b.Kind == BucketKind.Trading);
            var instrument = db.Instruments.First();
            db.Snapshots.Add(new Snapshot
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BucketId = tradingBucket.Id,
                Date = DateOnly.FromDateTime(DateTime.UtcNow),
                EquityMinor = 1_000_000,
                Currency = "ZAR",
            });
            for (var i = 0; i < 3; i++)
            {
                db.Trades.Add(new Trade
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    InstrumentId = instrument.Id,
                    BucketId = tradingBucket.Id,
                    Direction = TradeDirection.Long,
                    Status = TradeStatus.Closed,
                    OpenedAt = DateTime.UtcNow.AddHours(-2),
                    ClosedAt = DateTime.UtcNow,
                    Qty = 1,
                    AvgEntryPrice = 100,
                    AvgExitPrice = 90,
                    RealisedPnlMinor = -15_000,
                    RRealised = -1,
                    Currency = "ZAR",
                });
            }

            await db.SaveChangesAsync();
        }

        var status = await (await client.GetAsync("/api/cockpit/status")).ReadJsonAsync();
        var daily = status.GetProperty("reads").GetProperty("dailyPnl");
        daily.GetProperty("status").GetString().ShouldBe("red");
        daily.GetProperty("tripped").GetBoolean().ShouldBeTrue();
        daily.GetProperty("lossCount").GetInt32().ShouldBe(3);
        status.GetProperty("newPlanUnlocked").GetBoolean().ShouldBeFalse();
        status.GetProperty("allGreen").GetBoolean().ShouldBeFalse();

        // Reason is required.
        var noReason = await client.PostAsJsonAsync("/api/cockpit/override", new { reason = "" });
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Override unlocks for the day and logs CockpitRed + CircuitBreaker rows.
        var overridden = await client.PostAsJsonAsync(
            "/api/cockpit/override", new { reason = "A+ setup planned yesterday" });
        overridden.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await overridden.ReadJsonAsync();
        after.GetProperty("newPlanUnlocked").GetBoolean().ShouldBeTrue();
        after.GetProperty("activeOverride").GetProperty("reason").GetString()
            .ShouldBe("A+ setup planned yesterday");

        await using (var db = factory.CreateDbContext(userId))
        {
            var overrides = db.OverrideLogs.ToList();
            overrides.ShouldContain(o => o.Kind == OverrideKind.CockpitRed);
            overrides.ShouldContain(o => o.Kind == OverrideKind.CircuitBreaker);
        }

        // The override shows up in the overrides history endpoint too.
        var history = await (await client.GetAsync("/api/overrides?kind=cockpitRed")).ReadJsonAsync();
        history.GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Ladder_pauses_and_locks_at_15_percent_drawdown()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync(ApiClientHelpers.UniqueEmail());
        client.UseBearer(auth.AccessToken!);
        var userId = auth.User!.Id;

        await using (var db = factory.CreateDbContext(userId))
        {
            var tradingBucket = db.Buckets.Single(b => b.Kind == BucketKind.Trading);
            tradingBucket.HighWaterMarkMinor = 1_000_000;
            db.Snapshots.Add(new Snapshot
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BucketId = tradingBucket.Id,
                Date = DateOnly.FromDateTime(DateTime.UtcNow),
                EquityMinor = 800_000, // 20% drawdown
                Currency = "ZAR",
            });
            await db.SaveChangesAsync();
        }

        var status = await (await client.GetAsync("/api/cockpit/status")).ReadJsonAsync();
        var ladder = status.GetProperty("reads").GetProperty("ladder");
        ladder.GetProperty("status").GetString().ShouldBe("red");
        ladder.GetProperty("rung").GetString().ShouldBe("paused");
        ladder.GetProperty("locked").GetBoolean().ShouldBeTrue();
        status.GetProperty("newPlanUnlocked").GetBoolean().ShouldBeFalse();
    }
}
