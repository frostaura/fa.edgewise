using System.Net;
using System.Net.Http.Json;
using Edgewise.Api.Services.Portfolio;
using Edgewise.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Edgewise.Api.IntegrationTests.Portfolio;

[Collection(ApiCollection.Name)]
public sealed class LadderAndRatchetTests(TestAppFactory factory)
{
    /// <summary>Trading-bucket holding whose value is 100 x quote price (1 unit, cents).</summary>
    private async Task<(HttpClient Client, Guid UserId, Guid InstrumentId)> SetupTradingPositionAsync(
        long hwmMinor, decimal initialPrice)
    {
        var (client, userId) = await factory.NewUserClientAsync();
        var buckets = await client.GetBucketsAsync();
        var trading = buckets.Single(b => b.Kind == BucketKind.Trading);

        var instrument = await factory.SeedInstrumentAsync(currency: "ZAR", quotePrice: initialPrice);
        var holding = await client.CreateHoldingAsync(trading.Id, instrument.Id);
        await client.CreateLotAsync(holding.Id, 1m, hwmMinor, DateTime.UtcNow.AddMonths(-4));

        await using var db = factory.CreateDbContext(userId);
        var bucket = await db.Buckets.SingleAsync(b => b.Id == trading.Id);
        bucket.HighWaterMarkMinor = hwmMinor;
        await db.SaveChangesAsync();

        return (client, userId, instrument.Id);
    }

    private static async Task<LadderStateDto> GetLadderAsync(HttpClient client)
    {
        var ladder = await client.GetFromJsonAsync<LadderStateDto>(
            "/api/portfolio/ladder-state", PortfolioTestHelpers.Json);
        ladder.ShouldNotBeNull();
        return ladder;
    }

    [Fact]
    public async Task Ladder_walks_through_all_states_as_drawdown_deepens()
    {
        // HWM = R1000.00; price 1000 => equity 100000 minor.
        var (client, _, instrumentId) = await SetupTradingPositionAsync(hwmMinor: 100000, initialPrice: 1000m);

        (await GetLadderAsync(client)).State.ShouldBe(LadderState.Normal);

        await factory.SetQuoteAsync(instrumentId, 940m); // -6%
        var halved = await GetLadderAsync(client);
        halved.State.ShouldBe(LadderState.RiskHalved);
        halved.DrawdownPct.ShouldBe(0.06m, 0.0001m);

        await factory.SetQuoteAsync(instrumentId, 890m); // -11%
        (await GetLadderAsync(client)).State.ShouldBe(LadderState.Paused);

        await factory.SetQuoteAsync(instrumentId, 840m); // -16%
        var paper = await GetLadderAsync(client);
        paper.State.ShouldBe(LadderState.PaperProposed);
        paper.HwmMinor.ShouldBe(100000);
        paper.CurrentMinor.ShouldBe(84000);
        paper.Thresholds.RiskHalvedPct.ShouldBe(0.05m);
        paper.Thresholds.PausedPct.ShouldBe(0.10m);
        paper.Thresholds.PaperPct.ShouldBe(0.15m);
    }

    [Fact]
    public async Task Custom_thresholds_from_active_risk_profile_are_honoured()
    {
        var (client, userId, instrumentId) = await SetupTradingPositionAsync(hwmMinor: 100000, initialPrice: 1000m);

        await using (var db = factory.CreateDbContext(userId))
        {
            var active = await db.RiskProfiles.SingleAsync(r => r.IsActive);
            active.LadderThresholdsJson = """{"riskHalvedPct":0.02,"pausedPct":0.04,"paperPct":0.08}""";
            await db.SaveChangesAsync();
        }

        await factory.SetQuoteAsync(instrumentId, 950m); // -5% >= 4% paused threshold
        var ladder = await GetLadderAsync(client);
        ladder.State.ShouldBe(LadderState.Paused);
        ladder.Thresholds.PausedPct.ShouldBe(0.04m);
    }

    [Fact]
    public async Task Ratchet_suggestion_appears_above_hwm_and_accept_moves_profit_and_bumps_hwm()
    {
        // Equity 120000 over HWM 100000 => 20000 suggested.
        var (client, userId, _) = await SetupTradingPositionAsync(hwmMinor: 100000, initialPrice: 1200m);

        var suggestion = await client.GetFromJsonAsync<RatchetSuggestionDto>(
            "/api/portfolio/ratchet/suggestion", PortfolioTestHelpers.Json);
        suggestion!.SuggestedAmountMinor.ShouldBe(20000);
        suggestion.HwmMinor.ShouldBe(100000);
        suggestion.CurrentMinor.ShouldBe(120000);

        var accept = await client.PostAsJsonAsync(
            "/api/portfolio/ratchet/accept", new AcceptRatchetRequest(10000), PortfolioTestHelpers.Json);
        accept.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await accept.Content.ReadFromJsonAsync<AcceptRatchetResultDto>(PortfolioTestHelpers.Json))!;
        result.NewHighWaterMarkMinor.ShouldBe(110000); // 120000 - 10000, ratcheted up from 100000

        await using var db = factory.CreateDbContext(userId);
        var flows = await db.CashFlows.Where(f => f.Type == CashFlowType.Ratchet).ToListAsync();
        flows.Count.ShouldBe(2);
        var trading = await db.Buckets.SingleAsync(b => b.Kind == BucketKind.Trading);
        var longTerm = await db.Buckets.SingleAsync(b => b.Kind == BucketKind.LongTerm);
        flows.Single(f => f.BucketId == trading.Id).AmountMinor.ShouldBe(-10000);
        flows.Single(f => f.BucketId == longTerm.Id).AmountMinor.ShouldBe(10000);
        trading.HighWaterMarkMinor.ShouldBe(110000);

        // Suggestion goes quiet after a ratchet inside the quarter.
        var afterwards = await client.GetAsync("/api/portfolio/ratchet/suggestion");
        afterwards.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Summary_includes_buckets_ladder_and_drift()
    {
        var (client, _, _) = await SetupTradingPositionAsync(hwmMinor: 100000, initialPrice: 1000m);

        var summary = await client.GetFromJsonAsync<PortfolioSummaryDto>(
            "/api/portfolio/summary", PortfolioTestHelpers.Json);
        summary!.BaseCurrency.ShouldBe("ZAR");
        summary.TotalValueMinor.ShouldBe(100000);
        summary.PerBucket.Count.ShouldBe(4);
        summary.LadderState.State.ShouldBe(LadderState.Normal);

        // Everything sits in Trading (target 25%) => actual 100% => drift flagged.
        var trading = summary.PerBucket.Single(b => b.Kind == BucketKind.Trading);
        trading.ActualAllocPct.ShouldBe(1m, 0.0001m);
        trading.TargetAllocPct.ShouldBe(0.25m);
        trading.DriftFlag.ShouldBeTrue();

        var allocation = await client.GetFromJsonAsync<AllocationDto>(
            "/api/portfolio/allocation", PortfolioTestHelpers.Json);
        allocation!.TotalValueMinor.ShouldBe(100000);
        allocation.PerBucket.Single(s => s.Label == "Trading").Pct.ShouldBe(1m, 0.0001m);
        allocation.PerAssetClass.Single().Key.ShouldBe("equity");
    }
}
