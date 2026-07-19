using System.Net;
using System.Net.Http.Json;
using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Portfolio;

[Collection(ApiCollection.Name)]
public sealed class CashflowsAndDividendsTests(TestAppFactory factory)
{
    [Fact]
    public async Task Deposit_and_withdrawal_are_stored_signed()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var cash = buckets.Single(b => b.Kind == BucketKind.Cash);

        var deposit = await client.PostAsJsonAsync(
            "/api/cashflows",
            new CreateCashFlowRequest(CashFlowType.Deposit, cash.Id, null, 50000, null, null, "salary"),
            PortfolioTestHelpers.Json);
        deposit.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await deposit.Content.ReadFromJsonAsync<CashFlowDto>(PortfolioTestHelpers.Json))!
            .AmountMinor.ShouldBe(50000);

        var withdrawal = await client.PostAsJsonAsync(
            "/api/cashflows",
            new CreateCashFlowRequest(CashFlowType.Withdrawal, cash.Id, null, 20000, null, null, null),
            PortfolioTestHelpers.Json);
        (await withdrawal.Content.ReadFromJsonAsync<CashFlowDto>(PortfolioTestHelpers.Json))!
            .AmountMinor.ShouldBe(-20000);

        var list = await client.GetFromJsonAsync<List<CashFlowDto>>(
            $"/api/cashflows?bucketId={cash.Id}", PortfolioTestHelpers.Json);
        list!.Sum(f => f.AmountMinor).ShouldBe(30000);
    }

    [Fact]
    public async Task Transfer_creates_a_paired_row_in_each_bucket()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var cash = buckets.Single(b => b.Kind == BucketKind.Cash);
        var trading = buckets.Single(b => b.Kind == BucketKind.Trading);

        var response = await client.PostAsJsonAsync(
            "/api/cashflows",
            new CreateCashFlowRequest(CashFlowType.Transfer, cash.Id, trading.Id, 15000, null, null, null),
            PortfolioTestHelpers.Json);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var pair = (await response.Content.ReadFromJsonAsync<List<CashFlowDto>>(PortfolioTestHelpers.Json))!;

        pair.Count.ShouldBe(2);
        pair.Single(f => f.BucketId == cash.Id).AmountMinor.ShouldBe(-15000);
        pair.Single(f => f.BucketId == trading.Id).AmountMinor.ShouldBe(15000);
        pair.ShouldAllBe(f => f.Type == CashFlowType.Transfer);
    }

    [Fact]
    public async Task Cash_dividend_records_a_dividend_receipt_flow()
    {
        var (client, userId) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var longTerm = buckets.Single(b => b.Kind == BucketKind.LongTerm);

        var instrument = await factory.SeedInstrumentAsync(quotePrice: 50m);
        var holding = await client.CreateHoldingAsync(longTerm.Id, instrument.Id);
        await client.CreateLotAsync(holding.Id, 10m, 40000, DateTime.UtcNow.AddYears(-1));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var created = await client.PostAsJsonAsync(
            "/api/dividends",
            new CreateDividendRequest(holding.Id, today.AddDays(-10), today, 1200, null, false, null),
            PortfolioTestHelpers.Json);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        await using var db = factory.CreateDbContext(userId);
        var receipt = await db.CashFlows.SingleAsync(f => f.Type == CashFlowType.DividendReceipt);
        receipt.AmountMinor.ShouldBe(1200);
        receipt.BucketId.ShouldBe(longTerm.Id);
    }

    [Fact]
    public async Task Reinvested_dividend_creates_a_linked_drip_lot()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var longTerm = buckets.Single(b => b.Kind == BucketKind.LongTerm);

        var instrument = await factory.SeedInstrumentAsync(quotePrice: 50m);
        var holding = await client.CreateHoldingAsync(longTerm.Id, instrument.Id);
        await client.CreateLotAsync(holding.Id, 10m, 40000, DateTime.UtcNow.AddYears(-1));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var created = await client.PostAsJsonAsync(
            "/api/dividends",
            new CreateDividendRequest(holding.Id, today.AddDays(-10), today, 1000, null, true, 0.2m),
            PortfolioTestHelpers.Json);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var dividend = (await created.Content.ReadFromJsonAsync<DividendDto>(PortfolioTestHelpers.Json))!;
        dividend.DripLotId.ShouldNotBeNull();

        var lots = await client.GetFromJsonAsync<List<LotDto>>(
            $"/api/holdings/{holding.Id}/lots", PortfolioTestHelpers.Json);
        var dripLot = lots!.Single(l => l.Id == dividend.DripLotId);
        dripLot.Qty.ShouldBe(0.2m);
        dripLot.CostMinor.ShouldBe(1000);
        dripLot.Source.ShouldBe("drip");

        // Deleting the dividend removes the DRIP lot again.
        (await client.DeleteAsync($"/api/dividends/{dividend.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<List<LotDto>>(
            $"/api/holdings/{holding.Id}/lots", PortfolioTestHelpers.Json))!
            .ShouldNotContain(l => l.Id == dividend.DripLotId);
    }

    [Fact]
    public async Task Dividend_summary_reports_yield_on_cost_and_projection()
    {
        var (client, _) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var longTerm = buckets.Single(b => b.Kind == BucketKind.LongTerm);

        var instrument = await factory.SeedInstrumentAsync(quotePrice: 50m);
        var holding = await client.CreateHoldingAsync(longTerm.Id, instrument.Id);
        await client.CreateLotAsync(holding.Id, 10m, 100000, DateTime.UtcNow.AddYears(-2));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Two recent dividends and one outside the trailing year.
        foreach (var (payDate, amount) in new[]
                 {
                     (today.AddMonths(-2), 2000L),
                     (today.AddMonths(-8), 3000L),
                     (today.AddMonths(-15), 9000L),
                 })
        {
            var res = await client.PostAsJsonAsync(
                "/api/dividends",
                new CreateDividendRequest(holding.Id, payDate.AddDays(-14), payDate, amount, null, false, null),
                PortfolioTestHelpers.Json);
            res.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var summary = await client.GetFromJsonAsync<DividendSummaryDto>(
            "/api/dividends/summary", PortfolioTestHelpers.Json);
        var row = summary!.Holdings.Single(r => r.HoldingId == holding.Id);
        row.TotalReceivedMinor.ShouldBe(14000);
        row.Trailing12MoMinor.ShouldBe(5000);
        row.Projected12MoMinor.ShouldBe(5000);
        row.YieldOnCost.ShouldNotBeNull();
        row.YieldOnCost!.Value.ShouldBe(0.05m, 0.0001m); // 5000 / 100000
    }
}
