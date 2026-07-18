using System.Net;
using System.Net.Http.Json;
using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Portfolio;

[Collection(ApiCollection.Name)]
public sealed class SnapshotAndPerformanceTests(TestAppFactory factory)
{
    [Fact]
    public async Task Snapshot_run_is_idempotent_and_writes_bucket_plus_total_rows()
    {
        var (client, userId) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var longTerm = buckets.Single(b => b.Kind == BucketKind.LongTerm);
        var cash = buckets.Single(b => b.Kind == BucketKind.Cash);

        var instrument = await factory.SeedInstrumentAsync(currency: "ZAR", quotePrice: 200m);
        var holding = await client.CreateHoldingAsync(longTerm.Id, instrument.Id);
        await client.CreateLotAsync(holding.Id, 5m, 90000, DateTime.UtcNow.AddMonths(-6));

        // Today's deposit into the cash bucket becomes both cash equity and net flow.
        var deposit = await client.PostAsJsonAsync(
            "/api/cashflows",
            new CreateCashFlowRequest(CashFlowType.Deposit, cash.Id, null, 25000, null, DateTime.UtcNow, null),
            PortfolioTestHelpers.Json);
        deposit.StatusCode.ShouldBe(HttpStatusCode.Created);

        var first = await client.PostAsync("/api/portfolio/snapshot/run", null);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await client.PostAsync("/api/portfolio/snapshot/run", null);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = factory.CreateDbContext(userId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var snapshots = await db.Snapshots.Where(s => s.Date == today).ToListAsync();

        snapshots.Count.ShouldBe(5); // 4 buckets + total, not doubled by the re-run
        var totalRow = snapshots.Single(s => s.BucketId == null);
        totalRow.EquityMinor.ShouldBe(100000 + 25000); // 5 x R200 x 100c + cash deposit
        totalRow.NetFlowMinor.ShouldBe(25000);
        snapshots.Single(s => s.BucketId == longTerm.Id).EquityMinor.ShouldBe(100000);
        snapshots.Single(s => s.BucketId == cash.Id).EquityMinor.ShouldBe(25000);
    }

    [Fact]
    public async Task Twr_over_scripted_snapshots_shows_ten_percent()
    {
        var (client, userId) = await factory.NewUserClientAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var db = factory.CreateDbContext(userId))
        {
            db.Snapshots.AddRange(
                new Snapshot
                {
                    Id = Guid.NewGuid(), UserId = userId, BucketId = null,
                    Date = today.AddDays(-10), EquityMinor = 100000, Currency = "ZAR", NetFlowMinor = 0,
                },
                new Snapshot
                {
                    Id = Guid.NewGuid(), UserId = userId, BucketId = null,
                    Date = today.AddDays(-1), EquityMinor = 110000, Currency = "ZAR", NetFlowMinor = 0,
                });
            await db.SaveChangesAsync();
        }

        var perf = await client.GetFromJsonAsync<PerformanceDto>(
            "/api/portfolio/performance?basis=twr", PortfolioTestHelpers.Json);
        perf!.Basis.ShouldBe("twr");
        perf.TotalReturn.ShouldNotBeNull();
        perf.TotalReturn!.Value.ShouldBe(0.10m, 0.0001m);
        perf.Points.Count.ShouldBe(1);
        perf.Points[0].CumulativeReturn.ShouldBe(0.10m, 0.0001m);
        perf.MaxDrawdown.ShouldBe(0m);
    }

    [Fact]
    public async Task Twr_treats_deposits_as_external_flows_not_gains()
    {
        var (client, userId) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var trading = buckets.Single(b => b.Kind == BucketKind.Trading);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var db = factory.CreateDbContext(userId))
        {
            db.Snapshots.AddRange(
                new Snapshot
                {
                    Id = Guid.NewGuid(), UserId = userId, BucketId = null,
                    Date = today.AddDays(-5), EquityMinor = 100000, Currency = "ZAR", NetFlowMinor = 0,
                },
                new Snapshot
                {
                    Id = Guid.NewGuid(), UserId = userId, BucketId = null,
                    Date = today.AddDays(-1), EquityMinor = 150000, Currency = "ZAR", NetFlowMinor = 50000,
                });
            await db.SaveChangesAsync();
        }

        // The 50k arrived as a deposit two days ago, not as market gains.
        var deposit = await client.PostAsJsonAsync(
            "/api/cashflows",
            new CreateCashFlowRequest(
                CashFlowType.Deposit, trading.Id, null, 50000, null, DateTime.UtcNow.AddDays(-2), null),
            PortfolioTestHelpers.Json);
        deposit.StatusCode.ShouldBe(HttpStatusCode.Created);

        var perf = await client.GetFromJsonAsync<PerformanceDto>(
            "/api/portfolio/performance?basis=twr", PortfolioTestHelpers.Json);
        perf!.TotalReturn.ShouldNotBeNull();
        perf.TotalReturn!.Value.ShouldBe(0m, 0.0001m);
    }

    [Fact]
    public async Task Mwr_xirr_matches_a_one_year_ten_percent_gain()
    {
        var (client, userId) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var longTerm = buckets.Single(b => b.Kind == BucketKind.LongTerm);

        // Invested 100k a year ago...
        var deposit = await client.PostAsJsonAsync(
            "/api/cashflows",
            new CreateCashFlowRequest(
                CashFlowType.Deposit, longTerm.Id, null, 100000, null, DateTime.UtcNow.AddDays(-365), null),
            PortfolioTestHelpers.Json);
        deposit.StatusCode.ShouldBe(HttpStatusCode.Created);

        // ...now worth 110k (quoted holding).
        var instrument = await factory.SeedInstrumentAsync(currency: "ZAR", quotePrice: 1100m);
        var holding = await client.CreateHoldingAsync(longTerm.Id, instrument.Id);
        await client.CreateLotAsync(holding.Id, 1m, 100000, DateTime.UtcNow.AddDays(-365));

        var perf = await client.GetFromJsonAsync<PerformanceDto>(
            "/api/portfolio/performance?basis=mwr", PortfolioTestHelpers.Json);
        perf!.Basis.ShouldBe("mwr");
        perf.Xirr.ShouldNotBeNull();
        perf.Xirr!.Value.ShouldBe(0.10m, 0.01m);
    }

    [Fact]
    public async Task Networth_series_returns_total_rows_with_flow_annotations()
    {
        var (client, userId) = await factory.NewUserClientAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var db = factory.CreateDbContext(userId))
        {
            db.Snapshots.AddRange(
                new Snapshot
                {
                    Id = Guid.NewGuid(), UserId = userId, BucketId = null,
                    Date = today.AddDays(-2), EquityMinor = 50000, Currency = "ZAR", NetFlowMinor = 50000,
                },
                new Snapshot
                {
                    Id = Guid.NewGuid(), UserId = userId, BucketId = null,
                    Date = today.AddDays(-1), EquityMinor = 52000, Currency = "ZAR", NetFlowMinor = 0,
                });
            await db.SaveChangesAsync();
        }

        var series = await client.GetFromJsonAsync<List<NetworthPointDto>>(
            "/api/portfolio/networth", PortfolioTestHelpers.Json);
        series!.Count.ShouldBe(2);
        series[0].NetFlowMinor.ShouldBe(50000);
        series[1].EquityMinor.ShouldBe(52000);
    }
}
